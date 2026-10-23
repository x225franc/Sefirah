using System.Text;

namespace Sefirah.Utils;

/// <summary>One instance per TCP connection; UTF-8 characters may span receive callbacks.</summary>
public sealed class WireMessageBuffer
{
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder pending = new();
    private int scanPosition;

    public void Append(byte[] bytes, int offset, int count)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
        var written = decoder.GetChars(bytes, offset, count, chars, 0);
        pending.Append(chars, 0, written);
    }

    public List<string> ReadMessages()
    {
        List<string> messages = [];
        int consumed = 0;
        for (int i = scanPosition; i < pending.Length; i++)
        {
            if (pending[i] != '\n') continue;
            var message = pending.ToString(consumed, i - consumed).Trim();
            if (message.Length > 0) messages.Add(message);
            consumed = i + 1;
        }
        pending.Remove(0, consumed);
        scanPosition = pending.Length;
        return messages;
    }
}
