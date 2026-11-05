using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace SeamlessSwap.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestAttribute : Attribute { }

    internal sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    internal static class Check
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new AssertionException(message);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new AssertionException($"{what}: expected {expected}, got {actual}");
            }
        }
    }

    internal static class Program
    {
        private static async Task<int> Main()
        {
            int passed = 0;
            var failures = new List<string>();
            var tests = Assembly.GetExecutingAssembly().GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name);

            foreach (var test in tests)
            {
                string name = test.DeclaringType.Name + "." + test.Name;
                try
                {
                    if (test.Invoke(null, null) is Task task)
                    {
                        await task;
                    }
                    passed++;
                    Console.WriteLine("  ok    " + name);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
                    failures.Add(name + ": " + inner.Message);
                    Console.WriteLine("  FAIL  " + name + ": " + inner.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{passed} passed, {failures.Count} failed");
            return failures.Count == 0 ? 0 : 1;
        }
    }
}
