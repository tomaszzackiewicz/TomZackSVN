using System;
using System.Text;

namespace SVN.Core
{
    public static class SVNErrorHelper
    {
        public static bool IsInapplicableOrObstruction(Exception ex)
        {
            string msg = GetFullExceptionMessage(ex);
            return msg.Contains("W195024") ||
                   msg.Contains("E155027") ||
                   msg.Contains("Inapplicable conflict resolution option", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("obstructed", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E155025") || msg.Contains("E155010") || msg.Contains("E155011") ||
                   msg.Contains("E155012") || msg.Contains("E155015") || msg.Contains("E155016") ||
                   msg.Contains("E155017") || msg.Contains("W195012");
        }

        public static string GetFullExceptionMessage(Exception ex)
        {
            if (ex == null) return "";
            var sb = new StringBuilder(ex.Message ?? "");
            var inner = ex.InnerException;
            while (inner != null)
            {
                sb.Append(" | ").Append(inner.Message);
                inner = inner.InnerException;
            }
            return sb.ToString();
        }

        public static string GetShortError(Exception ex)
        {
            string msg = GetFullExceptionMessage(ex);
            int idx = msg.IndexOf('\n');
            return idx > 0 ? msg.Substring(0, idx).Trim() : msg.Trim();
        }
    }
}