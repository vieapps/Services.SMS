#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.SMS
{
	public class ServiceComponent : ServiceBase
	{
		Dictionary<string, Sender> Senders { get; } = new Dictionary<string, Sender>(StringComparer.OrdinalIgnoreCase);

		Sender Default { get; set; }

		public override string ServiceName => "SMS";

		public override void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			if (ConfigurationManager.GetSection("net.vieapps.services.sms.senders") is AppConfigurationSectionHandler config)
			{
				if (config.Section.SelectNodes("./sender") is XmlNodeList cfgSenders)
					cfgSenders.ToList().ForEach(cfgSender =>
					{
						var sender = new Sender
						(
							cfgSender.Attributes["name"].Value.Trim().ToLower(),
							cfgSender.Attributes["provider"].Value.Trim().ToLower(),
							new Uri(cfgSender.Attributes["uri"]?.Value ?? "https://smsgateway.vieapps.net"),
							cfgSender.Attributes["method"]?.Value ?? "POST"
						);
						cfgSender.SelectNodes("./parameters/parameter")?.ToList().ForEach(svcParameter =>
						{
							var name = svcParameter.Attributes["name"]?.Value;
							var value = svcParameter.Attributes["value"]?.Value;
							if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
								sender.Parameters[name] = value;
						});
						this.Senders[sender.Name] = sender;
					});
				var @default = config.Section.Attributes["default"]?.Value;
				this.Default = !string.IsNullOrWhiteSpace(@default) && this.Senders.ContainsKey(@default)
					? this.Senders[@default]
					: this.Senders.Values.FirstOrDefault();
			}
			base.Start(args, false, _ =>
			{
				next?.Invoke(this);
				this.Logger?.LogInformation($"Senders (default: {this.Default?.Name ?? "None"}):\r\n\t- {this.Senders.ToString("\r\n\t- ", kvp => $"{kvp.Key}: {kvp.Value.Provider} [{kvp.Value.Uri}] - Parameters => {(kvp.Value.Parameters.Any() ? kvp.Value.Parameters.ToString(", ", parameter => $"{parameter.Key}: {parameter.Value}") : "None")}")}");
			});
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var stopwatch = Stopwatch.StartNew();
			await this.WriteLogsAsync(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})").ConfigureAwait(false);
			try
			{
				if (!requestInfo.Verb.IsEquals("POST"))
					throw new MethodNotAllowedException(requestInfo.Verb);

				var name = requestInfo.GetParameter("x-sms-sender") ?? requestInfo.ObjectName;
				name = string.IsNullOrWhiteSpace(name) || !this.Senders.ContainsKey(name)
					? this.Default?.Name
					: name.ToLower();

				if (string.IsNullOrWhiteSpace(name))
					throw new InformationNotFoundException("No suitable SMS sender was found");

				JToken json = null;
				switch (this.Senders[name].Provider)
				{
					case "vnpay":
						json = await this.SendSmsViaVnPayAsync(requestInfo, name, cancellationToken).ConfigureAwait(false);
						break;
				}

				await this.WriteLogsAsync(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo, $"- Request: {requestInfo.ToString(this.JsonFormat)}" + "\r\n" + $"- Response: {json?.ToString(this.JsonFormat)}").ConfigureAwait(false);

				return json;
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex, stopwatch);
			}
		}

		async Task<JObject> SendSmsViaVnPayAsync(RequestInfo requestInfo, string name, CancellationToken cancellationToken)
		{
			var request = requestInfo.GetBodyExpando();
			var phone = request.Get<string>("Phone") ?? request.Get<string>("phone") ?? request.Get<string>("destination");
			var message = request.Get<string>("Message") ?? request.Get<string>("message") ?? request.Get<string>("shortMessage");
			if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(message))
				throw new InformationRequiredException("Phone number/Message is required");

			var sender = this.Senders[name];
			var payload = request.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

			payload.Remove("Phone");
			payload.Remove("Message");

			if (message.Contains("{{") && message.Contains("}}") && requestInfo.Extra != null && requestInfo.Extra.Any())
				message = message.Format(requestInfo.Extra.ToDictionary(kvp => kvp.Key, kvp => kvp.Value as object, StringComparer.OrdinalIgnoreCase));

			sender.Parameters.ForEach(kvp => payload[kvp.Key] = kvp.Value);
			payload["messageId"] = UtilityService.NewUUID;
			payload["isEncrypt"] = 0;
			payload["type"] = 0;
			payload["requestTime"] = DateTime.Now.ToUnixTimestamp();
			payload["destination"] = phone.Replace("+", "");
			payload["shortMessage"] = message.ConvertUnicodeToANSI();

			var requestJson = payload.ToJson();
			using (var httpResponse = await sender.Uri.SendHttpRequestAsync(sender.Method, new Dictionary<string, string> { ["content-type"] = "application/json" }, requestJson.ToString(Newtonsoft.Json.Formatting.None), 45, cancellationToken).ConfigureAwait(false))
			{
				using (var stream = httpResponse.GetResponseStream())
				{
					var response = await stream.ReadAllAsync(cancellationToken).ConfigureAwait(false);
					var responseJson = (string.IsNullOrWhiteSpace(response) ? "{\"status\":\"00\"}" : response).ToJson();
					if (!"00".IsEquals(responseJson.Get<string>("status")))
					{
						await this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.GetDeveloperID(), requestInfo.GetAppID(), $"Error occurred while sending a SMS message via VnPay (sender: {name})\r\n- Request: {requestJson.ToString(this.JsonFormat)}" + "\r\n" + $"- Response: {responseJson.ToString(this.JsonFormat)}", null, this.ServiceName, "VnPay", LogLevel.Error).ConfigureAwait(false);
						throw new InformationInvalidException(responseJson.Get<string>("description"));
					}
					else if (this.IsDebugLogEnabled)
						await this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.GetDeveloperID(), requestInfo.GetAppID(), $"Send a SMS message via VnPay successful (sender: {name})\r\n- Request: {requestJson.ToString(this.JsonFormat)}" + "\r\n" + $"- Response: {responseJson.ToString(this.JsonFormat)}", null, this.ServiceName, "VnPay", LogLevel.Debug).ConfigureAwait(false);
				}
			}

			return new JObject
			{
				{ "Status", "Sent" }
			};
		}
	}

	internal class Sender
	{
		public Sender(string name = null, string provider = null, Uri uri = null, string method = null)
		{
			this.Name = name;
			this.Provider = provider;
			this.Uri = uri;
			this.Method = method;
		}

		public string Name { get; set; }

		public string Provider { get; set; }

		public Uri Uri { get; set; }

		public string Method { get; set; }

		public Dictionary<string, string> Parameters { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}
}