namespace Andromeld.Protocol;

public static class ConnectCode
{
    private const string Alphabet = "123456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string Ticket(string input)
    {
        string code = string.Concat(input.ToUpperInvariant().Where(static c => c != '-' && !char.IsWhiteSpace(c)))
            .Replace('I', '1').Replace('L', '1');
        if (code.Length != 12 || code.Any(static c => !Alphabet.Contains(c)))
        {
            throw new FormatException("连接码应为手机显示的 12 位字符。");
        }

        int sum = 0;
        ulong bits = 0;
        for (int i = 0; i < 11; i++)
        {
            bits = (bits << 5) | (uint)Alphabet.IndexOf(code[i]);
        }

        for (int i = 10; i >= 0; i--)
        {
            int product = Alphabet.IndexOf(code[i]) * ((10 - i) % 2 == 0 ? 2 : 1);
            sum += (product / 32) + (product % 32);
        }

        if (Alphabet[(32 - (sum % 32)) % 32] != code[11] || (bits & 1) != 0)
        {
            throw new FormatException("连接码校验失败，请检查输入。");
        }

        ulong ticket = (bits >> 1) & 0xffffffffffUL;
        char[] result = new char[8];
        for (int i = 7; i >= 0; i--)
        {
            result[i] = Alphabet[(int)(ticket & 31)];
            ticket >>= 5;
        }

        return new string(result);
    }
}