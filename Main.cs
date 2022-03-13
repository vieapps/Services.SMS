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

		int Timeout { get; } = Int32.TryParse(UtilityService.GetAppSetting("SMS:Timeout", "45"), out var timeout) && timeout > 0 ? timeout : 45;

		public override string ServiceName => "SMS";

		public override void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			if (ConfigurationManager.GetSection("net.vieapps.services.sms.senders") is AppConfigurationSectionHandler config)
			{
				if (config.Section.SelectNodes("./sender") is XmlNodeList cfgSenders)
					cfgSenders.ToList().ForEach(cfgSender =>
					{
						var sender = new Sender(cfgSender.Attributes["name"].Value.Trim().ToLower(), cfgSender.Attributes["provider"].Value.Trim().ToLower());
						this.Senders[sender.Name] = sender;

						cfgSender.SelectNodes("./parameters/add")?.ToList().ForEach(svcParameter =>
						{
							var name = svcParameter.Attributes["name"]?.Value;
							var value = svcParameter.Attributes["value"]?.Value;
							if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
								sender.Parameters[name] = value;
						});

						cfgSender.SelectNodes("./headers/add")?.ToList().ForEach(svcHeader =>
						{
							var name = svcHeader.Attributes["name"]?.Value;
							var value = svcHeader.Attributes["value"]?.Value;
							if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
								sender.Headers[name] = value;
						});

						var url = cfgSender.Attributes["url"]?.Value ?? "https://smsgateway.vieapps.net";
						if (url.Contains("{{") && url.Contains("}}"))
							url = url.Format(sender.Parameters.Concat(sender.Headers).ToDictionary(kvp => kvp.Key, kvp => kvp.Value as object, StringComparer.OrdinalIgnoreCase));

						sender.Uri = new Uri(url);
						sender.Method = cfgSender.Attributes["method"]?.Value ?? "POST";
					});
				var @default = config.Section.Attributes["default"]?.Value;
				this.Default = !string.IsNullOrWhiteSpace(@default) && this.Senders.ContainsKey(@default)
					? this.Senders[@default]
					: this.Senders.Values.FirstOrDefault();
			}
			this.Syncable = false;
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

					case "twilio":
						json = await this.SendSmsViaTwilioAsync(requestInfo, name, cancellationToken).ConfigureAwait(false);
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
			var requestHeaders = new Dictionary<string, string>(sender.Headers, StringComparer.OrdinalIgnoreCase)
			{
				["Content-Type"] = "application/json; charset=utf-8"
			};

			using (var httpResponse = await sender.Uri.SendHttpRequestAsync(sender.Method, requestHeaders, requestJson.ToString(Newtonsoft.Json.Formatting.None), this.Timeout, cancellationToken).ConfigureAwait(false))
			{
				var response = await httpResponse.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				var responseJson = (string.IsNullOrWhiteSpace(response) ? "{\"status\":\"00\"}" : response).ToJson();
				if (!"00".IsEquals(responseJson.Get<string>("status")))
				{
					await this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.GetDeveloperID(), requestInfo.GetAppID(), $"Error occurred while sending a SMS message via VnPay (sender: {name})\r\n- Request:\r\n\t- Headers:\r\n\t\t{requestHeaders.ToString("\r\n\t\t", kvp => $"{kvp.Key}: {kvp.Value}")}\r\n\t- Boby: {requestJson.ToString(this.JsonFormat)}\r\n- Response: {responseJson.ToString(this.JsonFormat)}", null, this.ServiceName, "VnPay", LogLevel.Error).ConfigureAwait(false);
					throw new InformationInvalidException(responseJson.Get<string>("description"));
				}
				else if (this.IsDebugLogEnabled)
					await this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.GetDeveloperID(), requestInfo.GetAppID(), $"Send a SMS message via VnPay successful (sender: {name})\r\n- Request:\r\n\t- Headers:\r\n\t\t{requestHeaders.ToString("\r\n\t\t", kvp => $"{kvp.Key}: {kvp.Value}")}\r\n\t- Boby: {requestJson.ToString(this.JsonFormat)}\r\n- Response: {responseJson.ToString(this.JsonFormat)}", null, this.ServiceName, "VnPay", LogLevel.Debug).ConfigureAwait(false);
			}

			return new JObject
			{
				{ "Status", "Sent" }
			};
		}

		async Task<JObject> SendSmsViaTwilioAsync(RequestInfo requestInfo, string name, CancellationToken cancellationToken)
		{
			var request = requestInfo.GetBodyExpando();
			var phone = request.Get<string>("Phone") ?? request.Get<string>("phone") ?? request.Get<string>("To") ?? request.Get<string>("to");
			var message = request.Get<string>("Message") ?? request.Get<string>("message") ?? request.Get<string>("Body") ?? request.Get<string>("body");
			if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(message))
				throw new InformationRequiredException("Phone number/Message is required");

			if (message.Contains("{{") && message.Contains("}}") && requestInfo.Extra != null && requestInfo.Extra.Any())
				message = message.Format(requestInfo.Extra.ToDictionary(kvp => kvp.Key, kvp => kvp.Value as object, StringComparer.OrdinalIgnoreCase));

			var sender = this.Senders[name];
			if (!sender.Parameters.TryGetValue("From", out var twilioPhoneNumber))
			{
				if (!sender.Parameters.TryGetValue("PhoneNumber", out twilioPhoneNumber))
					twilioPhoneNumber = phone;
			}

			var requestBody = $"Body={message.ConvertUnicodeToANSI().UrlEncode()}&From={twilioPhoneNumber.UrlEncode()}&To={phone.UrlEncode()}";
			var requestHeaders = new Dictionary<string, string>(sender.Headers, StringComparer.OrdinalIgnoreCase)
			{
				["Content-Type"] = "application/x-www-form-urlencoded; charset=utf-8"
			};

			using (var httpResponse = await sender.Uri.SendHttpRequestAsync(sender.Method, requestHeaders, requestBody, this.Timeout, cancellationToken).ConfigureAwait(false))
			{
				var response = await httpResponse.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				var responseJson = (string.IsNullOrWhiteSpace(response) ? "{\"code\":0}" : response).ToJson();
				if (responseJson.Get<int>("code") > 0)
				{
					await this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.GetDeveloperID(), requestInfo.GetAppID(), $"Error occurred while sending a SMS message via Twilio (sender: {name})\r\n- Request:\r\n\t- Headers:\r\n\t\t{requestHeaders.ToString("\r\n\t\t", kvp => $"{kvp.Key}: {kvp.Value}")}\r\n\t- Boby: {requestBody}\r\n- Response: {responseJson.ToString(this.JsonFormat)}", null, this.ServiceName, "Twilio", LogLevel.Error).ConfigureAwait(false);
					throw new InformationInvalidException(responseJson.Get<string>("message"));
				}
				else if (this.IsDebugLogEnabled)
					await this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.GetDeveloperID(), requestInfo.GetAppID(), $"Send a SMS message via Twilio successful (sender: {name})\r\n- Request:\r\n\t- Headers:\r\n\t\t{requestHeaders.ToString("\r\n\t\t", kvp => $"{kvp.Key}: {kvp.Value}")}\r\n\t- Boby: {requestBody}\r\n- Response: {responseJson.ToString(this.JsonFormat)}", null, this.ServiceName, "Twilio", LogLevel.Debug).ConfigureAwait(false);
			}

			return new JObject
			{
				{ "Status", "Sent" }
			};
		}
	}

	internal class Sender
	{
		public Sender(string name = null, string provider = null)
		{
			this.Name = name;
			this.Provider = provider;
		}

		public string Name { get; set; }

		public string Provider { get; set; }

		public Uri Uri { get; set; }

		public string Method { get; set; }

		public Dictionary<string, string> Parameters { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}
}