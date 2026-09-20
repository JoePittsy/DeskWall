using DeskWall.Core.Shortcuts;
using Xunit;

public class BlankIconTests
{
    [Fact]
    public void Ensure_Writes_A_Valid_Single_Image_Png_Ico_Once()
    {
        var p = BlankIcon.Ensure();
        var b = File.ReadAllBytes(p);
        Assert.Equal(0, BitConverter.ToUInt16(b, 0));     // reserved
        Assert.Equal(1, BitConverter.ToUInt16(b, 2));     // type icon
        Assert.Equal(1, BitConverter.ToUInt16(b, 4));     // one image
        Assert.Equal(0, b[6]); Assert.Equal(0, b[7]);     // 256 x 256
        Assert.Equal(32, BitConverter.ToUInt16(b, 12));   // bpp
        var size = BitConverter.ToInt32(b, 14); var offset = BitConverter.ToInt32(b, 18);
        Assert.Equal(22, offset);
        Assert.Equal(b.Length, offset + size);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, b[offset..(offset + 4)]);   // PNG signature
        var t1 = File.GetLastWriteTimeUtc(p);
        Assert.Equal(p, BlankIcon.Ensure());
        Assert.Equal(t1, File.GetLastWriteTimeUtc(p));    // not rewritten
    }
}
