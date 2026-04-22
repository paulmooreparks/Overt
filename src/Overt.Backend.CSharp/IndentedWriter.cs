using System.Text;

namespace Overt.Backend.CSharp;

/// <summary>
/// Minimal indent-tracking text builder. Two-space indent; <see cref="Indent"/> returns
/// an IDisposable so callers can scope indent with <c>using</c>.
/// </summary>
public sealed class IndentedWriter
{
    private readonly StringBuilder _sb;
    private int _depth;
    private bool _atLineStart = true;

    public IndentedWriter(StringBuilder sb) { _sb = sb; }

    public IDisposable Indent()
    {
        _depth++;
        return new Releaser(this);
    }

    public void Write(string text)
    {
        if (_atLineStart && text.Length > 0)
        {
            _sb.Append(' ', _depth * 4);
            _atLineStart = false;
        }
        _sb.Append(text);
    }

    public void WriteLine(string text = "")
    {
        Write(text);
        _sb.Append('\n');
        _atLineStart = true;
    }

    private sealed class Releaser : IDisposable
    {
        private readonly IndentedWriter _parent;
        public Releaser(IndentedWriter parent) { _parent = parent; }
        public void Dispose() => _parent._depth--;
    }
}
