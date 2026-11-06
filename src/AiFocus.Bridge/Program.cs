using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace AiFocus.Bridge;

internal static class Program
{
    private const int MaxMessageBytes = 1024 * 1024;

    private static async Task Main()
    {
        // Native Messaging uses stdout as a binary protocol. Never log to stdout.
        try
        {
            using var pipe = new NamedPipeClientStream(".", "AiFocus-" + Environment.UserName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3500);
            using var nativeInput = Console.OpenStandardInput();
            using var nativeOutput = Console.OpenStandardOutput();
            using var pipeReader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var pipeWriter = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            using var stop = new CancellationTokenSource();
            var toUi = Task.Run(async () =>
            {
                var header = new byte[4];
                while (!stop.Token.IsCancellationRequested)
                {
                    await nativeInput.ReadExactlyAsync(header, stop.Token);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length <= 0 || length > MaxMessageBytes) break;
                    var payload = new byte[length];
                    await nativeInput.ReadExactlyAsync(payload, stop.Token);
                    await pipeWriter.WriteLineAsync(Encoding.UTF8.GetString(payload));
                }
            }, stop.Token);

            var toBrowser = Task.Run(async () =>
            {
                while (!stop.Token.IsCancellationRequested)
                {
                    var line = await pipeReader.ReadLineAsync(stop.Token);
                    if (line == null) break;
                    var body = Encoding.UTF8.GetBytes(line);
                    if (body.Length > MaxMessageBytes) continue;
                    var header = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
                    await nativeOutput.WriteAsync(header, stop.Token);
                    await nativeOutput.WriteAsync(body, stop.Token);
                    await nativeOutput.FlushAsync(stop.Token);
                }
            }, stop.Token);
            await Task.WhenAny(toUi, toBrowser);
            stop.Cancel();
        }
        catch (Exception ex)
        {
            // stderr does not corrupt the Native Messaging stream.
            Console.Error.WriteLine("AI Focus bridge disconnected: " + ex.Message);
        }
    }
}
