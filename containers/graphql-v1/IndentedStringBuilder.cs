using System.Text;

internal sealed class IndentedStringBuilder {
	private readonly StringBuilder _sb = new();

	private int _indent = 0;

	public void Append(char c) {
		_sb.Append(c);
	}

	public void Append(string s) {
		_sb.Append(s);
	}

	public void AppendLine(string s) {
		AppendIndents();
		_sb.AppendLine(s);
	}

	public void AppendLine() {
		AppendIndents();
		_sb.AppendLine();
	}

	public void AppendIndents() {
		_sb.Append(new string('\t', _indent));
	}

	public void Indent() => _indent++;
	public void Unindent() => _indent--;

	public override string ToString() => _sb.ToString();
}