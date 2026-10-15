// The interop UnityEngine.CoreModule re-exports stripped copies of these compiler attributes (without
// constructors), which shadows the BCL ones. Types from the current compilation win, so define them here.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class IsUnmanagedAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public NullableAttribute(byte b) { }
        public NullableAttribute(byte[] b) { }
    }

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public NullableContextAttribute(byte b) { }
    }
}
