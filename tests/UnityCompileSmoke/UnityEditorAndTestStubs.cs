using System;

namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    public delegate void TestDelegate();

    public abstract class Constraint
    {
    }

    public sealed class EqualConstraint : Constraint
    {
        public EqualConstraint(object expected)
        {
        }
    }

    public sealed class BooleanConstraint : Constraint
    {
    }

    public sealed class ZeroConstraint : Constraint
    {
    }

    public static class Is
    {
        public static Constraint True { get; } = new BooleanConstraint();

        public static Constraint False { get; } = new BooleanConstraint();

        public static Constraint Zero { get; } = new ZeroConstraint();

        public static Constraint EqualTo(object expected)
        {
            return new EqualConstraint(expected);
        }
    }

    public static class Assert
    {
        public static void That<TActual>(
            TActual actual,
            Constraint expression)
        {
        }

        public static TException Catch<TException>(TestDelegate code)
            where TException : Exception
        {
            throw new NotSupportedException(
                "Compile-only NUnit stub.");
        }

        public static TException Throws<TException>(TestDelegate code)
            where TException : Exception
        {
            throw new NotSupportedException(
                "Compile-only NUnit stub.");
        }
    }
}

namespace UnityEngine.TestTools
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnityTestAttribute : Attribute
    {
    }
}

namespace UnityEditor
{
    public enum ScriptingImplementation
    {
        Mono2x,
        IL2CPP
    }

    public enum BuildTargetGroup
    {
        Standalone
    }

    public enum BuildTarget
    {
        StandaloneWindows64
    }

    [Flags]
    public enum BuildOptions
    {
        None = 0,
        Development = 1,
        IncludeTestAssemblies = 2
    }

    public sealed class BuildPlayerOptions
    {
        public string[] scenes;
        public string locationPathName;
        public BuildTarget target;
        public BuildOptions options;
    }

    public static class PlayerSettings
    {
        public static ScriptingImplementation GetScriptingBackend(
            BuildTargetGroup buildTargetGroup)
        {
            return ScriptingImplementation.Mono2x;
        }

        public static void SetScriptingBackend(
            BuildTargetGroup buildTargetGroup,
            ScriptingImplementation backend)
        {
        }
    }

    public static class BuildPipeline
    {
        public static Build.Reporting.BuildReport BuildPlayer(
            BuildPlayerOptions options)
        {
            return new Build.Reporting.BuildReport();
        }
    }

    public static class AssetDatabase
    {
        public static T LoadAssetAtPath<T>(string path)
            where T : class
        {
            return null;
        }

        public static bool DeleteAsset(string path)
        {
            return true;
        }
    }

    public sealed class SceneAsset
    {
    }
}

namespace UnityEditor.Build.Reporting
{
    public enum BuildResult
    {
        Unknown,
        Succeeded,
        Failed,
        Cancelled
    }

    public sealed class BuildSummary
    {
        public BuildResult result = BuildResult.Succeeded;
        public int totalErrors;
    }

    public sealed class BuildReport
    {
        public BuildSummary summary = new BuildSummary();
    }
}

namespace UnityEditor.SceneManagement
{
    public enum NewSceneSetup
    {
        EmptyScene
    }

    public enum NewSceneMode
    {
        Single
    }

    public sealed class Scene
    {
    }

    public struct SceneSetup
    {
    }

    public static class EditorSceneManager
    {
        public static SceneSetup[] GetSceneManagerSetup()
        {
            return Array.Empty<SceneSetup>();
        }

        public static void RestoreSceneManagerSetup(SceneSetup[] value)
        {
        }

        public static Scene NewScene(
            NewSceneSetup setup,
            NewSceneMode mode)
        {
            return new Scene();
        }

        public static bool SaveScene(Scene scene, string path)
        {
            return true;
        }
    }
}
