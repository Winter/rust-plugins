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
        _client = new RedisClient(_config.Host, _config.Port, _config.Password)
        {
            MaximumQueueSize = _config.MaximumQueueSize,
            ConnectionDelay = TimeSpan.FromMilliseconds(_config.ConnectionDelayMs),
            DequeueDelay = TimeSpan.FromMilliseconds(_config.DequeueDelayMs)
        };

        _client.OnConnected += () =>
        {
            Puts($"RedisClient: Connected to {_config.Host}:{_config.Port}");
        };

        _client.OnAuthenticated += () =>
        {
            Puts($"RedisClient: Authenticated");
        };

        _client.OnDisconnected += () =>
        {
            Puts($"RedisClient: Disconnected from {_config.Host}:{_config.Port}");
        };

        await _client.ConnectAsync();
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
        public int ConnectionDelayMs = 5000;
        public int DequeueDelayMs = 50;

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

/// <summary>
///     Represents a Redis client that can execute Redis commands over a network connection.
/// </summary>
public class RedisClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;

    private readonly ConcurrentQueue<string> _queue;

    private CancellationTokenSource _cancellationTokenSource;
    private TcpClient _client;
    private NetworkStream _stream;
    private bool _disposed;
    private bool _running;

    public int MaximumQueueSize { get; set; } = 100000;
    public TimeSpan ConnectionDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DequeueDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     Occurs when the Redis client successfully connects
    /// </summary>
    public event Action OnConnected;

    /// <summary>
    ///     Occurs when the Redis client successfully authenticated
    /// </summary>
    /// 
    public event Action OnAuthenticated;

    /// <summary>
    ///     Occurs when the Redis client is disconnected 
    /// </summary>
    public event Action OnDisconnected;

    /// <summary>
    /// Occurs when an error occurs in the Redis client
    /// </summary>    
    public event Action<Exception> OnError;

    /// <summary>
    ///     Initializes a new instance of RedisClient with the specified host and port number.
    /// </summary>
    /// <param name="host">The hostname or IP address of the Redis server.</param>
    /// <param name="port">The port number on which the Redis server is listening.</param>
    public RedisClient(
        string host,
        int port,
        string password)
    {
        _host = host;
        _port = port;
        _password = password;
        _queue = new ConcurrentQueue<string>();
    }

    /// <summary>
    ///     Connects the Redis client to the Redis server.
    /// </summary>
    /// <returns>A task that represents the asynchronous connect operation.</returns>
    public async Task ConnectAsync()
    {
        _cancellationTokenSource = new CancellationTokenSource();

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_host, _port);
                _stream = _client.GetStream();

                if (_password is not null)
                {
                    // Send our password to the Redis server
                    var result = await ExecuteAsync("AUTH", _password);

                    if (result.StartsWith("+OK"))
                    {
                        OnAuthenticated?.Invoke();
                    }
                    else if (result.StartsWith("-ERR"))
                    {
                        OnError?.Invoke(new Exception(result));

                        // Wait some time before trying to connect again
                        await Task.Delay(ConnectionDelay);
                        continue;
                    }
                }

                // We're likely coming from ReconnectAsync. Don't start the SendMessagesAsync loop again.
                if (!_running)
                {
                    // Start sending messages from our queue in the background
                    _ = SendMessagesAsync();
                }

                OnConnected?.Invoke();

                return;
            }
            catch (SocketException)
            {
                // Wait some time before trying to connect again
                await Task.Delay(ConnectionDelay);
            }
        }
    }

    public async Task SendMessagesAsync()
    {
        _running = true;

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            // Apparently, while loops treat their scope like a for loop
            while (_queue.TryDequeue(out var message))
            {
                try
                {
                    // Send our message
                    await SendAsync(message);
                }
                catch (IOException)
                {
                    // We've encountered a snag. Attempt to reconnect.
                    await ReconnectAsync();
                }
            }

            // We've reached the end of our queue. Let's wait.
            await Task.Delay(DequeueDelay);
        }
    }

    /// <summary>
    ///     Receive a response from the Redis server
    /// </summary>
    /// <returns>A task that represents the asynchronous receive operation. The result of the task is the Redis server's response</returns>
    /// <exception cref="EndOfStreamException" />
    public async Task<string> ReceiveResponseAsync()
    {
        var buffer = new byte[1024];
        var responseStream = new MemoryStream();

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            // Fill our buffer
            var count = await _stream.ReadAsync(buffer, 0, buffer.Length);

            // We didn't receive anything. Bad things happened.
            if (count == 0)
            {
                throw new EndOfStreamException("The connection was closed by the remote Redis server");
            }

            // Fill our responseStream with the contents of the buffer
            responseStream.Write(buffer, 0, count);

            if (responseStream.Length > 1)
            {
                // Move our cursor one before the null terminator
                count--;

                // Check to see if the last two characters were a CRLF
                if (buffer[count--] == '\n' && buffer[count--] == '\r')
                {
                    return Encoding.ASCII.GetString(responseStream.GetBuffer());
                }
            }
        }

        return "";
    }

    /// <summary>
    ///     Immediately sends a Redis command to the Redis server without reading the response.
    /// </summary>
    /// <param name="command">The Redis command to send</param>
    /// <param name="args">Arguments to provide the Redis command</param>
    /// <returns>A task that represents the asynchronous command operation</returns>
    public Task SendAsync(string command, params object[] args)
    {
        var bytes = Encoding.ASCII.GetBytes(BuildRespCommand(command, args));

        return _stream.WriteAsync(bytes, 0, bytes.Length);
    }

    /// <summary>
    ///     Immediately sends a Redis command to the Redis server without reading the response.
    /// </summary>
    /// <param name="respCommand">The Redis RESP formatted command to send</param>
    /// <returns>A task that represents the asynchronous command operation</returns>
    public Task SendAsync(string respCommand)
    {
        var bytes = Encoding.ASCII.GetBytes(respCommand);

        return _stream.WriteAsync(bytes, 0, bytes.Length);
    }

    /// <summary>
    ///     Sends a Redis command to the Redis server and receive the Redis server's response.
    /// </summary>
    /// <param name="command">Redis command to send</param>
    /// <param name="args">Arguments to provide the Redis command</param>
    /// <returns>A task that represents the asynchronous command operation. The result of the task is the Redis server's response</returns>
    public async Task<string> ExecuteAsync(string command, params object[] args)
    {
        await SendAsync(command, args);

        return await ReceiveResponseAsync();
    }

    /// <summary>
    ///     Enqueues a Redis command to be sent to the Reids server.
    /// </summary>
    /// <remarks>
    ///     This command utilizies a backing ConcurrentQueue to store the built messages.
    ///     A soft limit has been put in the event that something goes wrong and the
    ///     queue is not able to be empied. The limit is controllable with MaximumQueueSize.
    /// </remarks>
    /// <param name="command">Redis command to send</param>
    /// <param name="args">Arguments to provide the Redis command</param>
    public void QueueSend(string command, params object[] args)
    {
        if (_queue.Count > MaximumQueueSize)
        {
            return;
        }

        _queue.Enqueue(BuildRespCommand(command, args));
    }

    /// <summary>
    ///     Build a message in the RESP format.
    /// </summary>
    /// <param name="command">Redis command to build</param>
    /// <param name="args">Arguments to provide the Redis command</param>
    /// <returns>A string of the built command</returns>
    public string BuildRespCommand(string command, params object[] args)
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

    /// <summary>
    ///     Disconnects the Redis client from the Redis server and releases all network resources.
    /// </summary>
    public void Disconnect()
    {
        _cancellationTokenSource.Cancel();
        _client?.Close();
        _stream?.Dispose();

        OnDisconnected?.Invoke();
    }

    /// <summary>
    ///     Attempts to reconnect the Redis client to the Redis server.
    /// </summary>
    /// <returns>A task that represents the asynchronous reconnect operation.</returns>
    public async Task ReconnectAsync()
    {
        Disconnect();
        await ConnectAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Disconnect();
    }
}

#endif