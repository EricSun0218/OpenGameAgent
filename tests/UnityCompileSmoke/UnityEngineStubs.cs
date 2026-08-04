using System;

namespace UnityEngine
{
    public class Object
    {
        public static void Destroy(Object target)
        {
        }

        public static void DestroyImmediate(Object target)
        {
        }

        public static void DontDestroyOnLoad(Object target)
        {
        }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; }
    }

    public class Behaviour : Component
    {
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public sealed class WaitForSecondsRealtime
    {
        public WaitForSecondsRealtime(float seconds)
        {
        }
    }

    public class GameObject : Object
    {
        public GameObject(string name)
        {
            this.name = name;
        }

        public string name { get; set; }

        public T AddComponent<T>()
            where T : Component, new()
        {
            var component = new T();
            component.gameObject = this;
            return component;
        }
    }

    public static class Debug
    {
        public static void Log(object message)
        {
        }

        public static void LogException(Exception exception)
        {
        }
    }

    public static class Application
    {
        public static bool isPlaying
        {
            get { return true; }
        }

        public static string persistentDataPath
        {
            get { return "."; }
        }

        public static void Quit(int exitCode)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MinAttribute : Attribute
    {
        public MinAttribute(float minimum)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DefaultExecutionOrder : Attribute
    {
        public DefaultExecutionOrder(int order)
        {
        }
    }

    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad,
        BeforeSceneLoad,
        AfterAssembliesLoaded,
        BeforeSplashScreen,
        SubsystemRegistration
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute()
        {
        }

        public RuntimeInitializeOnLoadMethodAttribute(
            RuntimeInitializeLoadType loadType)
        {
        }
    }
}

namespace UnityEngine.Scripting
{
    [AttributeUsage(
        AttributeTargets.Class
        | AttributeTargets.Struct
        | AttributeTargets.Method
        | AttributeTargets.Constructor)]
    public class PreserveAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class AlwaysLinkAssemblyAttribute : Attribute
    {
    }
}
