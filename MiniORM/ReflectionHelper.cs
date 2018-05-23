namespace MiniORM
{
	using System;
	using System.Linq;
	using System.Reflection;
	using JetBrains.Annotations;

	internal static class ReflectionHelper
	{
		public static object InvokeStaticGenericMethod(Type type, string methodName, Type genericType, params object[] args)
		{
			var method = type
				.GetMethod(methodName)
				.MakeGenericMethod(genericType);

			var invokeResult = method.Invoke(null, args);
			return invokeResult;
		}

		public static void ReplaceBackingField(object sourceObj, string propertyName, object targetObj)
		{
			var backingField = sourceObj.GetType()
				.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.SetField)
				.First(fi => fi.Name == $"<{propertyName}>k__BackingField");

			backingField.SetValue(sourceObj, targetObj);
		}

		public static bool HasAttribute<T>([NotNull] MemberInfo mi)
			where T : Attribute
		{
			var hasAttribute = mi.GetCustomAttribute<T>() != null;
			return hasAttribute;
		}
	}
}