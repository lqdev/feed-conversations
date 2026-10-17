using FeedConversations.Api.Infrastructure;

namespace FeedConversations.Api.Portability;

internal sealed class ArchiveBuffer(long maximum) : MemoryStream
{
    private void Check(int count)
    {
        if (Math.Max(Length, Position + count) > maximum) throw new ApiProblem(413, "archive-too-large", "The full archive exceeds its byte limit.");
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
}
