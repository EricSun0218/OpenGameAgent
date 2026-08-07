using System;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AddComponentMenu : Attribute
    {
        public AddComponentMenu(string menuName)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute
    {
    }

    public class Object
    {
        public static void DestroyImmediate(Object target)
        {
        }
    }

    public class MonoBehaviour : Object
    {
    }

    public sealed class GameObject : Object
    {
        public GameObject(string name)
        {
        }

        public T AddComponent<T>() where T : new() => new T();
    }
}

namespace UnityEngine.Events
{
    [Serializable]
    public class UnityEvent<TFirst, TSecond>
    {
        public void Invoke(TFirst first, TSecond second)
        {
        }
    }
}
