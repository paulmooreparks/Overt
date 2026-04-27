// Bytes — the foundational immutable byte sequence. Pairs with
// File.read_bytes / File.write_bytes for binary I/O.

namespace Overt.Runtime;

/// <summary>
/// Immutable byte sequence — the foundational binary-data type. Wraps
/// .NET's byte[] inside an immutable shell; programs read but don't
/// mutate. Used by File.read_bytes / write_bytes and any extern that
/// crosses a binary boundary. Bytes.at returns Int (0..255); a separate
/// Byte primitive would duplicate a refinement type the language
/// already supports (<c>type Byte = Int where 0 &lt;= self &amp;&amp; self &lt;= 150</c>).
/// <para>
/// Static methods live on the record itself rather than a separate
/// non-generic companion (Bytes is non-generic, so the
/// different-arity coexistence trick used by List / Map / Set doesn't
/// apply). Records can carry static members directly.
/// </para>
/// </summary>
public sealed record Bytes(byte[] Items)
{
    public static Bytes empty()
        => new(System.Array.Empty<byte>());

    public static Bytes from_list(List<int> list)
    {
        var data = new byte[list.Items.Length];
        for (var i = 0; i < list.Items.Length; i++)
        {
            var v = list.Items[i];
            if ((uint)v > 255)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(list), v,
                    $"Bytes.from_list: element at index {i} is {v}, expected 0..255");
            }
            data[i] = (byte)v;
        }
        return new(data);
    }

    public static int size(Bytes b) => b.Items.Length;

    public static int at(Bytes b, int index)
    {
        if ((uint)index >= (uint)b.Items.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index,
                $"Bytes.at: index out of range [0, {b.Items.Length})");
        }
        return b.Items[index];
    }

    public static Bytes slice(Bytes b, int start, int end)
    {
        if ((uint)start > (uint)b.Items.Length || (uint)end > (uint)b.Items.Length || start > end)
        {
            throw new ArgumentOutOfRangeException(
                $"Bytes.slice: indices out of range or inverted "
                + $"(start={start}, end={end}, length={b.Items.Length})");
        }
        var len = end - start;
        var data = new byte[len];
        System.Array.Copy(b.Items, start, data, 0, len);
        return new(data);
    }

    public static Bytes concat(Bytes left, Bytes right)
    {
        var data = new byte[left.Items.Length + right.Items.Length];
        System.Array.Copy(left.Items, 0, data, 0, left.Items.Length);
        System.Array.Copy(right.Items, 0, data, left.Items.Length, right.Items.Length);
        return new(data);
    }

    public static Bytes from_utf8(string s)
        => new(System.Text.Encoding.UTF8.GetBytes(s));

    public static Result<string, IoError> to_utf8(Bytes b)
    {
        try
        {
            return new ResultOk<string, IoError>(
                System.Text.Encoding.UTF8.GetString(b.Items));
        }
        catch (Exception ex)
        {
            return new ResultErr<string, IoError>(new IoError(ex.Message));
        }
    }
}
