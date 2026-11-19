using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TgwsOne.Core.Zapret.Windows;

/// <summary>
/// ProgramData's default ACL lets any user create files under a new folder. The service runs whatever it finds in
/// <c>strategies\</c> as SYSTEM, so on every start it takes the folder over: SYSTEM and Administrators may do
/// everything, ordinary users may only read and traverse.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceAcl
{
    public static void Protect(string root)
    {
        Directory.CreateDirectory(root);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);   // drop what ProgramData would pass down
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(Rule(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl, inherit));
        security.AddAccessRule(Rule(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, inherit));
        security.AddAccessRule(Rule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute, inherit));
        new DirectoryInfo(root).SetAccessControl(security);
    }

    private static FileSystemAccessRule Rule(WellKnownSidType sid, FileSystemRights rights, InheritanceFlags inherit)
        => new(new SecurityIdentifier(sid, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow);
}
