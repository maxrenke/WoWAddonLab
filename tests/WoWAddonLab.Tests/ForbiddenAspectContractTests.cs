using WoWAddonLab.Emulator;

namespace WoWAddonLab.Tests;

public sealed class ForbiddenAspectContractTests
{
    [Fact]
    public void ScriptObjectsTrackForbiddenAspectsAndAccessRestrictions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wow-addon-lab-aspects-{Guid.NewGuid():N}");
        var addon = Path.Combine(root, "Aspects");
        Directory.CreateDirectory(addon);
        File.WriteAllText(Path.Combine(addon, "Aspects.toc"), "## Interface: 16001\n## Title: Aspects\nmain.lua\n");
        File.WriteAllText(
            Path.Combine(addon, "main.lua"),
            "AspectFrame = CreateFrame('Frame', 'AspectFrame')\n" +
            "AspectAnim = AspectFrame:CreateAnimationGroup()\n" +
            "AspectFrame:AddForbiddenAspects(4)\n" +
            "AspectAnim:AddForbiddenAspects(1)\n" +
            "AspectFrame:AddAccessRestrictions(2)\n");
        try
        {
            using var session = new EmulatorSession();
            session.Load(addon, Path.Combine(root, "saved"));
            Assert.Equal("4", session.Lua.Evaluate("return tostring(AspectFrame:GetForbiddenAspects())"));
            Assert.Equal("true", session.Lua.Evaluate("return tostring(AspectFrame:HasAnyForbiddenAspects(4))"));
            Assert.Equal("false", session.Lua.Evaluate("return tostring(AspectFrame:HasAnyForbiddenAspects(1))"));
            Assert.Equal("true", session.Lua.Evaluate("return tostring(AspectAnim:HasAnyForbiddenAspects())"));
            Assert.Equal("true", session.Lua.Evaluate("return tostring(AspectFrame:HasAccessConstraints())"));
            Assert.Equal("true", session.Lua.Evaluate("return tostring(AspectFrame:GetObjectTable() == AspectFrame)"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
