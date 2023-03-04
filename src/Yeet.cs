#if CARBON

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        _client = new RedisClient(this)
        {
            ConnectionRetryDelay = TimeSpan.FromMilliseconds(_config.ConnectionRetryDelayMs)
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

        await _client.ConnectAsync(_config.Host, _config.Port, _config.Password);
    }

    public void Unload()
    {
        _client.Dispose();
    }

    public class PluginConfig
    {
        public string Name = "";
        public string Host = "";
        public int Port = 6379;
        public string Password = "";
        public int MaximumQueueSize = 100000;
        public int ConnectionRetryDelayMs = 5000;
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
    private readonly CarbonPlugin _plugin;
    private readonly ConcurrentQueue<string> _queue = new();
    private CancellationTokenSource _cancellationTokenSource;
    private Socket _socket;
    private NetworkStream _stream;
    private StreamReader _reader;
    private bool _disposed;
    private bool _running;

    private string _host;
    private int _port;
    private string _password;

    public bool Connected { get; internal set; }
    public int BatchSizeLimit { get; set; } = 10;
    public int QueueSizeLimit { get; set; } = 100000;
    public TimeSpan ConnectionRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MessageBatchDelay { get; set; } = TimeSpan.FromMilliseconds(5000);

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
    ///     Occurs when an error occurs in the Redis client
    /// </summary>    
    public event Action<string, Exception> OnError;

    public RedisClient(CarbonPlugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>
    ///     Connects to a Redis instance with the given host, port, and password.
    /// </summary>
    /// <param name="host">The host of the Redis instance</param>
    /// <param name="port">The port number of the Redis instance</param>
    /// <param name="password">(optional) The password to authenticate with the Redis instance</param>
    /// <returns>A Task that represents the asynchronous connect operation</returns>
    public async Task ConnectAsync(string host, int port, string password)
    {
        _host = host;
        _port = port;
        _password = password;
        _cancellationTokenSource = new CancellationTokenSource();

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await _socket.ConnectAsync(_host, _port);
                _stream = new NetworkStream(_socket);
                _reader = new StreamReader(_stream);

                if (_password is not null)
                {
                    var response = await ExecuteCommandAsync("AUTH", _password);

                    if (response.Type == RedisDataType.Error && response.Data is string error)
                    {
                        OnError?.Invoke(error, null);
                        OnDisconnected?.Invoke();
                        return;
                    }
                    else if (response.Type == RedisDataType.SimpleString && response.Data is string data)
                    {
                        if (data != "OK")
                        {
                            OnError?.Invoke($"Expected 'OK' received {data}", null);
                            OnDisconnected?.Invoke();
                            return;
                        }

                        OnAuthenticated?.Invoke();
                    }
                }

                Connected = true;
                OnConnected?.Invoke();

                if (!_running)
                {
                    _ = SendMessagesAsync();
                }

                return;
            }
            catch (SocketException)
            {
                await Task.Delay(ConnectionRetryDelay);
            }
        }
    }

    /// <summary>
    ///     Continuously sends queued Redis commands in batches, until cancellation is requested.
    /// </summary>
    public async Task SendMessagesAsync()
    {
        _running = true;

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            int batchCount = 0;

            while (Connected && batchCount < BatchSizeLimit && _queue.TryDequeue(out var redisCommand))
            {
                try
                {
                    await SendCommandAsync(redisCommand);
                    batchCount++;
                }
                catch (SocketException)
                {
                    await Reconnect();
                }
            }

            await Task.Delay(MessageBatchDelay);
        }
    }

    /// <summary>
    ///     Sends a Redis command to the Redis server asynchronously.
    /// </summary>
    /// <param name="redisCommand">The RESP formatted Redis command to send as a string</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <exception cref="ArgumentException">Thrown when redisCommand is null</exception>
    /// <exception cref="SocketException">Thrown when an error occurs when accessing the socket</exception>
    public Task SendCommandAsync(string redisCommand)
    {
        var buffer = Encoding.ASCII.GetBytes(redisCommand);
        return _socket.SendAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
    }

    /// <summary>
    ///     Receives data asynchronously from the Redis server and returns a RedisData object representing the response.
    /// </summary>
    /// <returns>A task representing the RedisData response from the Redis server</returns>
    /// <exception cref="SocketException">Thrown when an error occurs when accessing the socket</exception>
    /// <remarks>
    ///     The response from the Redis server is parsed according to the Redis Serialization Protocol (RESP) 
    ///     and returned as a RedisData object. The function reads data from the stream until a complete 
    ///     RedisData object is received, then returns the object to the calling function.
    /// </remarks>
    public async Task<RedisData> ReceiveAsync()
    {
        string data;

        var dataType = _reader.Read() switch
        {
            '+' => RedisDataType.SimpleString,
            '-' => RedisDataType.Error,
            ':' => RedisDataType.Integer,
            '$' => RedisDataType.BulkString,
            '*' => RedisDataType.Array,
            _ => RedisDataType.None
        };

        switch (dataType)
        {
            case RedisDataType.SimpleString:
            case RedisDataType.Error:
                data = await _reader.ReadLineAsync();
                return new RedisData(dataType, data);

            case RedisDataType.Integer:
                data = await _reader.ReadLineAsync();

                if (!long.TryParse(data, out var integer))
                {
                    return new RedisData(RedisDataType.Error, "Unable to parse data 'integer' as long");
                }
                return new RedisData(dataType, integer);

            case RedisDataType.BulkString:
                data = await _reader.ReadLineAsync();

                if (!int.TryParse(data, out var stringLength))
                {
                    return new RedisData(RedisDataType.Error, "Unable to parse data 'string length' as int");
                }

                // RESP Bulk Strings can return null 
                if (stringLength == -1)
                {
                    return new RedisData(dataType, "");
                }

                var buffer = new char[stringLength];

                await _reader.ReadAsync(buffer, 0, stringLength);
                await _reader.ReadLineAsync();

                return new RedisData(dataType, new string(buffer));

            case RedisDataType.Array:
                data = await _reader.ReadLineAsync();

                if (!int.TryParse(data, out var objectCount))
                {
                    return new RedisData(RedisDataType.Error, "Unable to parse data 'object count' as int");
                }

                if (objectCount == -1)
                {
                    return RedisData.None;
                }

                var list = new List<RedisData>();

                for (var i = 0; i < objectCount; i++)
                {
                    list.Add(await ReceiveAsync());
                }
                return new RedisData(dataType, list);

            default:
                return RedisData.None;
        }
    }

    /// <summary>
    ///     Executes a Redis command asynchronously with the specified command name and arguments.
    /// </summary>
    /// <param name="commandName">The name of the Redis command</param>
    /// <param name="args">The arguments for the Redis command</param>
    /// <returns>A task representing the RedisData response from the Redis server</returns>
    /// <remarks>
    ///     The Redis command is created using the CreateRedisCommand function and sent to the Redis 
    ///     server using the SendCommandAsync function. The response from the Redis server is received 
    ///     asynchronously using the ReceiveAsync function and returned as a RedisData object.
    /// </remarks>
    public async Task<RedisData> ExecuteCommandAsync(string commandName, params string[] args)
    {
        var redisCommand = CreateRedisCommand(commandName, args);

        await SendCommandAsync(redisCommand);

        return await ReceiveAsync();
    }

    /// <summary>
    ///     Queues a Redis command with the specified command name and arguments to be sent later.
    /// </summary>
    /// <param name="commandName">The name of the Redis command</param>
    /// <param name="args">The arguments for the Redis command</param>
    /// <remarks>
    ///     The Redis command is added to a queue to be executed later. If the queue size has been reached, 
    ///     an error message is sent using the OnError event handler and the command is not added to the queue.
    ///     Commands that have not been added to the queue are lost and cannot be recovered.
    /// </remarks>
    public void QueueCommand(string commandName, params string[] args)
    {
        if (_queue.Count > QueueSizeLimit)
        {
            OnError?.Invoke("The QueueSizeLimit has been reached", null);
            return;
        }

        var redisCommand = CreateRedisCommand(commandName, args);
        _queue.Enqueue(redisCommand);
    }

    /// <summary>
    ///     Creates a Redis command with the specified command name and arguments.
    /// </summary>
    /// <param name="commandName">The name of the Redis command</param>
    /// <param name="args">The arguments for the Redis command</param>
    /// <returns>A string Redis command formatted to the RESP protocol</returns>
    /// <remarks>
    ///     The Redis command is created in the format used by the Redis server protocol (RESP).
    ///     The format is: "*{number of arguments}\r\n${length of command name}\r\n{command name}\r\n${length of argument}\r\n{argument}\r\n".
    /// </remarks>
    public string CreateRedisCommand(string commandName, params string[] args)
    {
        var builder = new StringBuilder();

        // Append the command + args length
        builder.Append("*" + (args.Length + 1) + "\r\n");

        // Append the command length
        builder.Append("$" + commandName.Length + "\r\n");

        // Append the command
        builder.Append(commandName + "\r\n");

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
        Connected = false;

        _cancellationTokenSource?.Cancel();
        _socket?.Close();
        _stream?.Dispose();

        OnDisconnected?.Invoke();
    }

    /// <summary>
    ///     Attempts to reconnect the Redis client to the Redis server.
    /// </summary>
    /// <returns>A task that represents the asynchronous reconnect operation</returns>
    public Task Reconnect()
    {
        if (Connected)
        {
            Disconnect();
        }

        return ConnectAsync(_host, _port, _password);
    }

    /// <summary>
    ///     Releases all resources used by the RedisClient instance.
    /// </summary>
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

public enum RedisDataType
{
    None,
    SimpleString,
    Error,
    Integer,
    BulkString,
    Array
}

public class RedisData
{
    public static RedisData None => new(RedisDataType.None, null);

    public RedisDataType Type { get; }
    public object Data { get; }

    public RedisData(RedisDataType type, object data)
    {
        Type = type;
        Data = data;
    }
}

#endif