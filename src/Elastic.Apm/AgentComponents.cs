// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Elastic.Apm.Api;
using Elastic.Apm.BackendComm.CentralConfig;
using Elastic.Apm.Config;
using Elastic.Apm.DiagnosticListeners;
using Elastic.Apm.Features;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Metrics;
using Elastic.Apm.Metrics.MetricsProvider;
using Elastic.Apm.Report;
using Elastic.Apm.ServerInfo;
#if NET8_0_OR_GREATER
using Elastic.Apm.OpenTelemetry;
#endif

#if NETFRAMEWORK
using Elastic.Apm.Config.Net4FullFramework;
#endif

namespace Elastic.Apm
{
	public class AgentComponents : IApmAgent, IDisposable
	{
		public AgentComponents(
			IApmLogger logger = null,
			IConfigurationReader configurationReader = null,
			IPayloadSender payloadSender = null
		) : this(logger, configurationReader, payloadSender, null, null, null, null) { }

		internal AgentComponents(
			IApmLogger logger,
			IConfigurationReader configurationReader,
			IPayloadSender payloadSender,
			IMetricsCollector metricsCollector,
			ICurrentExecutionSegmentsContainer currentExecutionSegmentsContainer,
			ICentralConfigurationFetcher centralConfigurationFetcher,
			IApmServerInfo apmServerInfo,
			BreakdownMetricsProvider breakdownMetricsProvider = null,
			IHostNameDetector hostNameDetector = null
		)
		{
			try
			{
				var config = CreateConfiguration(logger, configurationReader);
				hostNameDetector ??= new HostNameDetector();

				Logger = logger ?? GetGlobalLogger(DefaultLogger(null, configurationReader), config.LogLevel);
				ConfigurationStore = new ConfigurationStore(new RuntimeConfigurationSnapshot(config), Logger);

				Service = Service.GetDefaultService(config, Logger);

				ApmServerInfo = apmServerInfo ?? new ApmServerInfo();
				HttpTraceConfiguration = new HttpTraceConfiguration();

#if NET8_0_OR_GREATER
				// Initialize early because ServerInfoCallback requires it and might execute
				// before EnsureElasticActivityStarted runs
				ElasticActivityListener = new ElasticActivityListener(this, HttpTraceConfiguration);
#endif
				var systemInfoHelper = new SystemInfoHelper(Logger);
				var system = systemInfoHelper.GetSystemInfo(Configuration.HostName, hostNameDetector);

				PayloadSender = payloadSender
					?? new PayloadSenderV2(Logger, ConfigurationStore.CurrentSnapshot, Service, system,
						ApmServerInfo,
						isEnabled: Configuration.Enabled, serverInfoCallback: ServerInfoCallback);

				if (Configuration.Enabled)
					breakdownMetricsProvider ??= new BreakdownMetricsProvider(Logger);

				// initialize the tracer before central configuration or metric collection is started
				TracerInternal = new Tracer(Logger, Service, PayloadSender, ConfigurationStore,
					currentExecutionSegmentsContainer ?? new CurrentExecutionSegmentsContainer(), ApmServerInfo,
					breakdownMetricsProvider);

#if NET8_0_OR_GREATER
				EnsureElasticActivityStarted();
#endif

				if (Configuration.Enabled)
				{
					var agentFeatures = AgentFeaturesProvider.Get(Logger);
					//
					// Central configuration
					//
					if (centralConfigurationFetcher != null)
						CentralConfigurationFetcher = centralConfigurationFetcher;
					else if (agentFeatures.Check(AgentFeature.RemoteConfiguration))
						CentralConfigurationFetcher = new CentralConfigurationFetcher(Logger, ConfigurationStore, Service);
					//
					// Metrics collection
					//
					if (metricsCollector != null)
						MetricsCollector = metricsCollector;
					else if (agentFeatures.Check(AgentFeature.MetricsCollection))
						MetricsCollector = new MetricsCollector(Logger, PayloadSender, ConfigurationStore, breakdownMetricsProvider);
					MetricsCollector?.StartCollecting();
				}
				else
				{
					Logger.Info()
						?.Log("The Elastic APM .NET Agent is disabled - the agent won't capture traces and metrics.");
				}
			}
			catch (Exception e)
			{
				Logger.Error()?.LogException(e, "Failed initializing agent.");
			}
		}

		private void EnsureElasticActivityStarted()
		{
#if !NET8_0_OR_GREATER
			return;
#else
			if (!Configuration.OpenTelemetryBridgeEnabled) return;

			// If the server version is not known yet, we enable the listener - and then the callback will do the version check once we have the version
			if (ApmServerInfo.Version == null || ApmServerInfo.Version == new ElasticVersion(0, 0, 0, null))
				ElasticActivityListener?.Start(TracerInternal);
			// Otherwise do a version check
			else if (ApmServerInfo.Version >= new ElasticVersion(7, 16, 0, string.Empty))
			{
				Logger.Info()?.Log("Starting OpenTelemetry (Activity) bridge");
				ElasticActivityListener?.Start(TracerInternal);
			}
			else
			{
				Logger.Warning()
					?.Log(
						"OpenTelemetry (Activity) bridge is only supported with APM Server 7.16.0 or newer - bridge won't be enabled. Current Server version: {version}",
						ApmServerInfo.Version.ToString());
				ElasticActivityListener?.Dispose();
			}
#endif
		}

		private void ServerInfoCallback(bool success, IApmServerInfo serverInfo)
		{
#if !NET8_0_OR_GREATER
			return;
#else
			if (!Configuration.OpenTelemetryBridgeEnabled) return;

			if (success)
			{
				if (serverInfo.Version >= new ElasticVersion(7, 16, 0, string.Empty))
				{
					Logger.Info()
						?.Log("APM Server version ready - OpenTelemetry (Activity) bridge is active. Current Server version: {version}",
							serverInfo.Version.ToString());
				}
				else
				{
					Logger.Warning()
						?.Log(
							"OpenTelemetry (Activity) bridge is only supported with APM Server 7.16.0 or newer - bridge won't be enabled. Current Server version: {version}",
							serverInfo.Version.ToString());
					ElasticActivityListener?.Dispose();
				}
			}
			else
			{
				Logger.Warning()
					?.Log(
						"Unable to read server version - OpenTelemetry (Activity) bridge is only supported with APM Server 7.16.0 or newer. "
						+ "The bridge remains active, but due to unknown server version it may not work as expected.");
			}
#endif
		}
#pragma warning disable IDE0022
		private static IApmLogger DefaultLogger(IApmLogger logger, IConfigurationReader configurationReader)
		{
#if NETFRAMEWORK
			return logger ?? FullFrameworkDefaultImplementations.CreateDefaultLogger(configurationReader?.LogLevel);
#else
			return logger ?? ConsoleLogger.LoggerOrDefault(configurationReader?.LogLevel);
#endif
		}

		private static IConfigurationReader CreateConfiguration(IApmLogger logger, IConfigurationReader configurationReader)
		{
			var configurationLogger = DefaultLogger(logger, configurationReader);

#if NETFRAMEWORK
			return configurationReader
					?? FullFrameworkDefaultImplementations.CreateConfigurationReaderFromConfiguredType(configurationLogger)
					?? new AppSettingsConfiguration(configurationLogger);
#else
			return configurationReader ?? new EnvironmentConfiguration(configurationLogger);
#endif
		}
#pragma warning restore IDE0022

		/// <summary>
		/// This ensures agents will respect externally provided loggers.
		/// <para>If the agent is started as part of profiling it should adhere to profiling configuration</para>
		/// <para>If file logging environment variables are set we should always log to that location</para>
		/// </summary>
		/// <param name="fallbackLogger"></param>
		/// <param name="agentLogLevel"></param>
		/// <param name="environmentVariables"></param>
		/// <returns></returns>
		internal static IApmLogger GetGlobalLogger(IApmLogger fallbackLogger, LogLevel agentLogLevel, IDictionary environmentVariables = null)
		{
			try
			{
				var fileLogConfig = GlobalLogConfiguration.FromEnvironment(environmentVariables);
				if (!fileLogConfig.IsActive)
				{
					fallbackLogger.Info()?.Log("No system wide logging configured, defaulting to fallback logger");
					return fallbackLogger;
				}

				var effectiveLogLevel = LogLevelUtils.GetFinest(agentLogLevel, fileLogConfig.LogLevel);
				if ((fileLogConfig.LogTargets & GlobalLogTarget.File) == GlobalLogTarget.File)
					TraceLogger.TraceSource.Listeners.Add(new TextWriterTraceListener(fileLogConfig.AgentLogFilePath));
				if ((fileLogConfig.LogTargets & GlobalLogTarget.StdOut) == GlobalLogTarget.StdOut)
					TraceLogger.TraceSource.Listeners.Add(new TextWriterTraceListener(Console.Out));

				var logger = new TraceLogger(effectiveLogLevel);
				logger.Info()?.Log($"{nameof(fileLogConfig)} - {fileLogConfig}");
				return logger;
			}
			catch (Exception e)
			{
				fallbackLogger.Warning()?.LogException(e, "Error in GetGlobalLogger");
			}
			return fallbackLogger;
		}

#if NET8_0_OR_GREATER
		private ElasticActivityListener ElasticActivityListener { get; }
#endif

		internal ICentralConfigurationFetcher CentralConfigurationFetcher { get; }

		internal IConfigurationStore ConfigurationStore { get; }

		internal IMetricsCollector MetricsCollector { get; }

		internal IApmServerInfo ApmServerInfo { get; }

		internal HttpTraceConfiguration HttpTraceConfiguration { get; }

		HashSet<Type> IApmAgentComponents.SubscribedListeners { get; } = new();

		internal Tracer TracerInternal { get; }

		[Obsolete("Please use Configuration property instead")]
		public IConfigurationReader ConfigurationReader => Configuration;

		public IConfigurationReader Configuration => ConfigurationStore.CurrentSnapshot;

		public IApmLogger Logger { get; }

		public IPayloadSender PayloadSender { get; }

		/// <summary>
		/// Identifies the monitored service. If this remains unset the agent
		/// automatically populates it based on the entry assembly.
		/// </summary>
		/// <value>The service.</value>
		public Service Service { get; }

		public ITracer Tracer => TracerInternal;

		public void Dispose()
		{
			if (MetricsCollector is IDisposable disposableMetricsCollector)
				disposableMetricsCollector.Dispose();

			if (PayloadSender is IDisposable disposablePayloadSender)
				disposablePayloadSender.Dispose();
			CentralConfigurationFetcher?.Dispose();
#if NET8_0_OR_GREATER
			ElasticActivityListener?.Dispose();
#endif
		}
	}
}
