using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace PawNoonLead
{
    /// <summary>Runs the EditMode tests and writes Lead/tests.log. Trigger: bridge "call PawNoonLead.LeadTests.Run".</summary>
    public static class LeadTests
    {
        private sealed class Cb : ICallbacks
        {
            private readonly StringBuilder sb = new StringBuilder();
            public void RunStarted(ITestAdaptor t) { sb.AppendLine("=== tests started " + System.DateTime.Now.ToString("HH:mm:ss")); }
            public void TestStarted(ITestAdaptor t) { }
            public void TestFinished(ITestResultAdaptor r)
            {
                if (r.Test.IsSuite) return;
                sb.AppendLine(r.TestStatus + " " + r.Test.FullName + (r.TestStatus == TestStatus.Failed ? "\n    " + r.Message?.Replace("\n", "\n    ") : ""));
            }
            public void RunFinished(ITestResultAdaptor r)
            {
                sb.AppendLine("=== " + r.TestStatus + " passed=" + r.PassCount + " failed=" + r.FailCount + " skipped=" + r.SkipCount);
                File.WriteAllText("Lead/tests.log", sb.ToString());
                Debug.Log("[LeadTests] done: " + r.TestStatus + " " + r.PassCount + "/" + (r.PassCount + r.FailCount));
            }
        }

        public static string Run()
        {
            File.WriteAllText("Lead/tests.log", "running\n");
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Cb());
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode }));
            return "ok tests started -> Lead/tests.log";
        }
    }
}
