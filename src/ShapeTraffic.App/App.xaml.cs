using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ShapeTraffic.App.ViewModels;
using ShapeTraffic.Core.Abstractions;
using ShapeTraffic.Infrastructure.Persistence;
using ShapeTraffic.Infrastructure.Services;

namespace ShapeTraffic.App;

public partial class App : Application
{
	private IHost? _host;
	private string? _logsDirectory;

	public App()
	{
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
	}

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		try
		{
			var appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShapeTraffic");
			var databasePath = Path.Combine(appDataDirectory, "traffic.sqlite");
			_logsDirectory = Path.Combine(appDataDirectory, "logs");
			Directory.CreateDirectory(_logsDirectory);

			Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Debug()
				.MinimumLevel.Override("Microsoft", LogEventLevel.Information)
				.WriteTo.File(Path.Combine(_logsDirectory, "shapetraffic-.log"), rollingInterval: RollingInterval.Day, shared: true)
				.CreateLogger();

			var baseDirectory = AppContext.BaseDirectory;
			Log.Information("ShapeTraffic starting. InteractiveSession={InteractiveSession}; BaseDirectory={BaseDirectory}; WinDivertDllExists={WinDivertDllExists}; WinDivertDriverExists={WinDivertDriverExists}",
				Environment.UserInteractive,
				baseDirectory,
				File.Exists(Path.Combine(baseDirectory, "WinDivert.dll")),
				File.Exists(Path.Combine(baseDirectory, "WinDivert64.sys")));

			var builder = Host.CreateApplicationBuilder();
			builder.Services.AddSingleton<ITrafficRepository>(serviceProvider =>
				new SqliteTrafficRepository(databasePath, serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteTrafficRepository>>()));
			builder.Services.AddSingleton<ITrafficController>(serviceProvider =>
				new WinDivertTrafficController(
					serviceProvider.GetRequiredService<ITrafficRepository>(),
					serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WinDivertTrafficController>>(),
					databasePath));
			builder.Services.AddSingleton(serviceProvider =>
				new MainViewModel(
					serviceProvider.GetRequiredService<ITrafficController>(),
					serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MainViewModel>>(),
					_logsDirectory));
			builder.Services.AddSingleton<MainWindow>();
			builder.Logging.ClearProviders();
			builder.Services.AddSerilog(Log.Logger, dispose: false);

			_host = builder.Build();
			await _host.StartAsync().ConfigureAwait(true);

			var mainWindow = _host.Services.GetRequiredService<MainWindow>();
			MainWindow = mainWindow;
			mainWindow.Show();
		}
		catch (Exception exception)
		{
			HandleFatalException("ShapeTraffic failed during startup.", exception);
			Shutdown(-1);
		}
	}

	protected override async void OnExit(ExitEventArgs e)
	{
		if (_host is not null)
		{
			var controller = _host.Services.GetRequiredService<ITrafficController>();
			await controller.StopAsync(CancellationToken.None).ConfigureAwait(true);
			await _host.StopAsync().ConfigureAwait(true);
			_host.Dispose();
		}

		Log.CloseAndFlush();
		base.OnExit(e);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		HandleFatalException("An unhandled UI exception caused ShapeTraffic to stop.", e.Exception);
		e.Handled = true;
		Shutdown(-1);
	}

	private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception exception)
		{
			HandleFatalException("A fatal application exception occurred.", exception);
		}
	}

	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		HandleFatalException("A background task failed unexpectedly.", e.Exception);
		e.SetObserved();
	}

	private void HandleFatalException(string title, Exception exception)
	{
		try
		{
			Log.Fatal(exception, title);
		}
		catch
		{
		}

		var logHint = string.IsNullOrWhiteSpace(_logsDirectory)
			? string.Empty
			: $"\n\nLogs: {_logsDirectory}";

		MessageBox.Show(
			$"{title}\n\n{exception.Message}{logHint}",
			"ShapeTraffic",
			MessageBoxButton.OK,
			MessageBoxImage.Error);
	}
}

