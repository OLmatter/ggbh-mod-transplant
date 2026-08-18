using System;
using System.IO;
using System.Reflection;
class SigTest {
    static void Main(string[] args) {
        string dir = @"C:\Users\17336\ZCodeProject\ggbh_work\dll";
        var asm = Assembly.LoadFrom(Path.Combine(dir, "Assembly-CSharp.dll"));
        var t = asm.GetType("WorldUnitLogMgr");
        foreach (var name in new[] { "MerageAddLog", "WriteLogData" }) {
            foreach (var m in t.GetMethods()) {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                Console.Write(m.ReturnType.Name + " " + m.Name + "(");
                foreach (var p in ps) Console.Write(p.ParameterType.FullName + " " + p.Name + (p.Position < ps.Length-1 ? ", " : ""));
                Console.WriteLine(")");
            }
        }
    }
}
