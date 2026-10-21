using System;

namespace ZeroMapper
{
    /// <summary>Generates implementations for partial mapping methods in this partial class.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class MapperAttribute : Attribute { }

    /// <summary>Prevents the mapper from assigning this destination property.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true)]
    public sealed class MapProtectedAttribute : Attribute { }

    /// <summary>Explicitly excludes a destination property from a mapping method.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MapIgnoreAttribute : Attribute
    {
        /// <summary>Creates an exclusion for an exact destination property name.</summary>
        public MapIgnoreAttribute(string destination) => Destination = destination;
        /// <summary>The destination property name.</summary>
        public string Destination { get; }
    }

    /// <summary>Allows one property to be written by an existing-object update method.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MapAllowAttribute : Attribute
    {
        /// <summary>Creates an allowance for an exact destination property name.</summary>
        public MapAllowAttribute(string destination) => Destination = destination;
        /// <summary>The destination property name.</summary>
        public string Destination { get; }
    }
}
