#if CARBON

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Carbon.Plugins;

[Info("Yeet", "Winter", "1.0.0")]
public class YeetPlugin : CarbonPlugin
{
    private PluginConfig _config = new();
    private RedisClient _client;

    protected override void LoadDefaultConfig()
    {
        Config.WriteObject(_config, true);
    }

    public void Init()
    {
        _config = Config.ReadObject<PluginConfig>();

        if (!PluginConfig.Validate(this, _config))
        {
            Puts("Unloading Yeet due to invalid configuration");
            Manager.RemovePlugin(this);
        }
    }

    public async Task Loaded()
    {
        _client = new RedisClient(this, _config.Host, _config.Port, _config.Password)
        {
            MaximumQueueSize = _config.MaximumQueueSize,
            ReconnectionDelay = TimeSpan.FromMilliseconds(_config.ReconnectionDelayMilliseconds),
            DequeueDelay = TimeSpan.FromMilliseconds(_config.DequeueDelayMilliseconds)
        };

        await _client.ConnectAsync();

        Puts("Connected!");
    }

    public void Unload()
    {
        _client?.Dispose();
    }

    public class PluginConfig
    {
        public string Name = "";
        public string Host = "";
        public int Port = 6379;
        public string Password = "";
        public int MaximumQueueSize = 100000;
        public int ReconnectionDelayMilliseconds = 5000;
        public int DequeueDelayMilliseconds = 50;

        public static bool Validate(CarbonPlugin plugin, PluginConfig config)
        {
            var isValid = true;

            if (string.IsNullOrEmpty(config.Name.Trim()))
            {
                plugin.Puts("'Name' cannot be null or empty.");
                isValid = false;
            }

            if (config.Name.Length > 16)
            {
                plugin.Puts("'Name' cannot be more than 16 characters.");
                isValid = false;
            }

            if (!Regex.IsMatch(config.Name, "[a-z0-9-_]"))
            {
                plugin.Puts("'Name' must match pattern '[a-z0-9-_]'");
                isValid = false;
            }

            if (string.IsNullOrEmpty(config.Host))
            {
                plugin.Puts("'Host' cannot be null or empty.");
                isValid = false;
            }

            if (config.Port <= 0 || config.Port >= 65536)
            {
                plugin.Puts("'Port' but be between 1 and 65535.");
            }

            return isValid;
        }
    }
}

public class RedisClient : IDisposable
{
    private readonly CarbonPlugin _plugin;
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;

    private TcpClient _client;
    private NetworkStream _stream;
    private StreamWriter _writer;

    private readonly CancellationTokenSource _cancellationSource;
    private readonly ConcurrentQueue<string> _queue;

    private bool _disposed;
    private bool _running;

    public int MaximumQueueSize { get; set; } = 100000;
    public TimeSpan ReconnectionDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DequeueDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    public RedisClient(
        CarbonPlugin plugin,
        string host,
        int port,
        string password)
    {
        _plugin = plugin;
        _host = host;
        _port = port;
        _password = password;

        _cancellationSource = new CancellationTokenSource();
        _queue = new ConcurrentQueue<string>();
    }

    public Task EnsureConnected()
    {
        if (_client == null || !_client.Connected)
        {
            return ConnectAsync();
        }

        return Task.CompletedTask;
    }

    public async Task ConnectAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RedisClient));
        }

        if (_client != null && _client.Connected)
        {
            return;
        }

        while (!_cancellationSource.IsCancellationRequested)
        {
            try
            {
                _client?.Dispose();
                _client = new TcpClient();

                await _client.ConnectAsync(_host, _port);

                _stream = _client.GetStream();
                _writer = new StreamWriter(_stream)
                {
                    AutoFlush = true
                };

                if (_password != null)
                {
                    // We don't want to queue our auth command as 
                    await SendCommandAsync("AUTH", _password);
                }

                if (!_running)
                {
                    // Unsure if this is fine. Carbon doesn't seem to let us use Task.Run.
                    // I feel as if there's some fuckery with SyncronizationContext's
                    _ = BeginSendAsync(_cancellationSource.Token);
                }

                return;
            }
            catch (TaskCanceledException)
            {
                if (!_disposed)
                {
                    _plugin.Puts($"A TaskCanceledException occurred yet we're not disposing?");
                }
            }
            catch (Exception ex)
            {
                _plugin.Puts($"An exception occurred while attempting to connect to {_host}:{_port}:\n{ex}");
                await Task.Delay(ReconnectionDelay);
            }
        }
    }

    public async Task BeginSendAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RedisClient));
        }

        _running = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnected();

                if (_queue.TryDequeue(out var respCommand))
                {
                    await _writer.WriteAsync(respCommand);
                }
                else
                {
                    await Task.Delay(DequeueDelay, cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
                if (!_disposed)
                {
                    _plugin.Puts($"A TaskCanceledException occurred yet we're not disposing?");
                }
            }
            catch (Exception ex)
            {
                _plugin.Puts($"An exception occurred while attempting to send command to {_host}:{_port}:\n{ex}");
                await Task.Delay(ReconnectionDelay, cancellationToken);
            }
        }
    }

    public async Task SendCommandAsync(string command, params string[] args)
    {
        await EnsureConnected();

        await _writer.WriteAsync(BuildRespCommand(command, args));
    }

    public void QueueCommand(string command, params string[] args)
    {
        if (_queue.Count > MaximumQueueSize)
        {
            return;
        }

        _queue.Enqueue(BuildRespCommand(command, args));
    }

    public string BuildRespCommand(string command, params string[] args)
    {
        var builder = new StringBuilder();

        // Append the command + args length
        builder.Append("*" + (args.Length + 1) + "\r\n");

        // Append the command length
        builder.Append("$" + command.Length + "\r\n");

        // Append the command
        builder.Append(command + "\r\n");

        foreach (string arg in args)
        {
            // Append the argument length
            builder.Append("$" + arg.Length + "\r\n");
            // Append the argument
            builder.Append(arg + "\r\n");
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationSource.Cancel();
        _client.Dispose();
    }
}

#endif