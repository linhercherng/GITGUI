using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace GitGui.Acceptance
{
    // Offline evidence processing only. No sampling, caching or excluded spans.
    public static class NativeSpanEvaluation
    {
        public sealed class Result
        {
            public int ProductSpans;
            public int CrossPhaseProductSpans;
            public double MaximumProductOverlapMs;
            public readonly Dictionary<string, int> Classifications = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public static int PhaseIndex(long[] starts, long timestamp)
        {
            int low = 0, high = starts.Length - 1, found = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (starts[middle] <= timestamp) { found = middle; low = middle + 1; }
                else high = middle - 1;
            }
            return found;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static long Integer(object value)
        {
            Require(value is ValueType && !(value is bool) && value is IConvertible, "Missing or nonnumeric span integer");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            Require(!double.IsNaN(number) && !double.IsInfinity(number) && number >= 1 &&
                number == Math.Floor(number) && number <= 9007199254740991d, "Invalid exact span integer");
            return (long)number;
        }

        public static Result Evaluate(object[] spans, long[] starts, string[] names, bool[] product, long uiThread, long frequency)
        {
            Require(starts.Length == names.Length && starts.Length == product.Length && starts.Length > 0 && frequency > 0, "Invalid phase table");
            for (int i = 1; i < starts.Length; i++) Require(starts[i] >= starts[i - 1], "Unordered phase table");
            var ids = new HashSet<long>();
            var seams = new HashSet<string>(StringComparer.Ordinal);
            var result = new Result();
            var required = new[] { "Id", "Completed", "EnterQpc", "ExitQpc", "NativeTid", "Seam", "ProductPhaseAtEnter", "ProductPhaseAtExit" };
            foreach (object item in spans)
            {
                var span = item as IDictionary;
                Require(span != null, "UI span must be an object");
                foreach (string field in required) Require(span.Contains(field), "Missing span field: " + field);
                long id = Integer(span["Id"]);
                Require(ids.Add(id), "Duplicate span id");
                Require(span["Completed"] is bool && (bool)span["Completed"], "Incomplete UI span");
                long enter = Integer(span["EnterQpc"]), exit = Integer(span["ExitQpc"]), thread = Integer(span["NativeTid"]);
                Require(exit >= enter, "Negative UI span");
                string seam = span["Seam"] as string;
                Require(seam == "DispatcherOperation.Execute" || seam == "Dispatcher.Invoke UI Send" || seam == "WindowImpl.AppWndProc", "Unknown profiler seam");
                int first = PhaseIndex(starts, enter), last = PhaseIndex(starts, exit);
                string enterPhase = first >= 0 ? names[first] : null;
                string exitPhase = last >= 0 ? names[last] : null;
                Require(object.Equals(span["ProductPhaseAtEnter"], enterPhase) && object.Equals(span["ProductPhaseAtExit"], exitPhase), "UI/product phase association mismatch");
                if (thread != uiThread) continue;
                seams.Add(seam);
                long overlap = 0;
                for (int j = Math.Max(0, first); j <= Math.Min(last, starts.Length - 2); j++)
                    if (product[j]) overlap += Math.Max(0L, Math.Min(exit, starts[j + 1]) - Math.Max(enter, starts[j]));
                string classification = overlap > 0 ? (enterPhase != exitPhase ? "cross-phase-product" : "product") : "setup-startup-report-only";
                int count; result.Classifications.TryGetValue(classification, out count); result.Classifications[classification] = count + 1;
                if (overlap <= 0) continue;
                result.ProductSpans++;
                if (classification == "cross-phase-product") result.CrossPhaseProductSpans++;
                double milliseconds = overlap * 1000d / frequency;
                result.MaximumProductOverlapMs = Math.Max(result.MaximumProductOverlapMs, milliseconds);
                Require(milliseconds <= 16, "UI span " + id + " (" + seam + ", " + classification + ") blocks product work for " + milliseconds.ToString("R", CultureInfo.InvariantCulture) + " ms >16");
            }
            Require(result.ProductSpans > 0 && seams.Contains("DispatcherOperation.Execute") && seams.Contains("WindowImpl.AppWndProc"), "No measured product dispatcher/native-window work");
            return result;
        }
    }
}
