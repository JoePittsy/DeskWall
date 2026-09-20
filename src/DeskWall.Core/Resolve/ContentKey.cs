using System.Security.Cryptography;
using System.Text;

namespace DeskWall.Core.Resolve;

public static class ContentKey
{
    public static string Of(Resolved c)
    {
        var sb = new StringBuilder();
        sb.Append(c.GetType().Name).Append('\u001F')
          .Append(c.Rect.X).Append(',').Append(c.Rect.Y).Append(',').Append(c.Rect.W).Append(',').Append(c.Rect.H).Append('\u001F')
          .Append(c.Z);
        foreach (var p in c.KeyParts()) sb.Append('\u001F').Append(p);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
