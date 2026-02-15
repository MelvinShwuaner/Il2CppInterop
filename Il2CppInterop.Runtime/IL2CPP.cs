using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime;

public static unsafe class IL2CPP
{
    private static readonly Dictionary<string, IntPtr> ourImagesMap = new();

    static IL2CPP()
    {
        var domain = il2cpp_domain_get();
        if (domain == IntPtr.Zero)
        {
            Logger.Instance.LogError("No il2cpp domain found; sad!");
            return;
        }

        uint assembliesCount = 0;
        var assemblies = il2cpp_domain_get_assemblies(domain, ref assembliesCount);
        for (var i = 0; i < assembliesCount; i++)
        {
            var image = il2cpp_assembly_get_image(assemblies[i]);
            var name = Marshal.PtrToStringAnsi(il2cpp_image_get_name(image));
            ourImagesMap[name] = image;
        }
    }

    internal static IntPtr GetIl2CppImage(string name)
    {
        if (ourImagesMap.ContainsKey(name)) return ourImagesMap[name];
        return IntPtr.Zero;
    }

    internal static IntPtr[] GetIl2CppImages()
    {
        return ourImagesMap.Values.ToArray();
    }

    public static IntPtr GetIl2CppClass(string assemblyName, string namespaze, string className)
    {
        if (!ourImagesMap.TryGetValue(assemblyName, out var image))
        {
            Logger.Instance.LogError("Assembly {AssemblyName} is not registered in il2cpp", assemblyName);
            return IntPtr.Zero;
        }

        var clazz = il2cpp_class_from_name(image, namespaze, className);
        return clazz;
    }

    public static IntPtr GetIl2CppField(IntPtr clazz, string fieldName)
    {
        if (clazz == IntPtr.Zero) return IntPtr.Zero;

        var field = il2cpp_class_get_field_from_name(clazz, fieldName);
        if (field == IntPtr.Zero)
            Logger.Instance.LogError(
                "Field {FieldName} was not found on class {ClassName}", fieldName, Marshal.PtrToStringUTF8(il2cpp_class_get_name(clazz)));
        return field;
    }

    public static IntPtr GetIl2CppMethodByToken(IntPtr clazz, int token)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(token.ToString());

        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
            if (il2cpp_method_get_token(method) == token)
                return method;

        var className = Marshal.PtrToStringAnsi(il2cpp_class_get_name(clazz));
        Logger.Instance.LogWarning("Unable to find method {ClassName}::{Token}", className, token);

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + token);
    }

    public static IntPtr GetIl2CppMethod(IntPtr clazz, bool isGeneric, string methodName, string returnTypeName,
        params string[] argTypes)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(methodName + "(" + string.Join(", ", argTypes) +
                                                                   ")");

        returnTypeName = Regex.Replace(returnTypeName, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        for (var index = 0; index < argTypes.Length; index++)
        {
            var argType = argTypes[index];
            argTypes[index] = Regex.Replace(argType, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        }

        var methodsSeen = 0;
        var lastMethod = IntPtr.Zero;
        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(il2cpp_method_get_name(method)) != methodName)
                continue;

            if (il2cpp_method_get_param_count(method) != argTypes.Length)
                continue;

            if (il2cpp_method_is_generic(method) != isGeneric)
                continue;

            var returnType = il2cpp_method_get_return_type(method);
            var returnTypeNameActual = Marshal.PtrToStringAnsi(il2cpp_type_get_name(returnType));
            if (returnTypeNameActual != returnTypeName)
                continue;

            methodsSeen++;
            lastMethod = method;

            var badType = false;
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                if (typeName != argTypes[i])
                {
                    badType = true;
                    break;
                }
            }

            if (badType) continue;

            return method;
        }

        var className = Marshal.PtrToStringAnsi(il2cpp_class_get_name(clazz));

        if (methodsSeen == 1)
        {
            Logger.Instance.LogTrace(
                "Method {ClassName}::{MethodName} was stubbed with a random matching method of the same name", className, methodName);
            Logger.Instance.LogTrace(
                "Stubby return type/target: {LastMethod} / {ReturnTypeName}", Marshal.PtrToStringUTF8(il2cpp_type_get_name(il2cpp_method_get_return_type(lastMethod))), returnTypeName);
            Logger.Instance.LogTrace("Stubby parameter types/targets follow:");
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(lastMethod, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                Logger.Instance.LogTrace("    {TypeName} / {ArgType}", typeName, argTypes[i]);
            }

            return lastMethod;
        }

        Logger.Instance.LogTrace("Unable to find method {ClassName}::{MethodName}; signature follows", className, methodName);
        Logger.Instance.LogTrace("    return {ReturnTypeName}", returnTypeName);
        foreach (var argType in argTypes)
            Logger.Instance.LogTrace("    {ArgType}", argType);
        Logger.Instance.LogTrace("Available methods of this name follow:");
        iter = IntPtr.Zero;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(il2cpp_method_get_name(method)) != methodName)
                continue;

            var nParams = il2cpp_method_get_param_count(method);
            Logger.Instance.LogTrace("Method starts");
            Logger.Instance.LogTrace(
                "     return {MethodTypeName}", Marshal.PtrToStringUTF8(il2cpp_type_get_name(il2cpp_method_get_return_type(method))));
            for (var i = 0; i < nParams; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = Marshal.PtrToStringAnsi(il2cpp_type_get_name(paramType));
                Logger.Instance.LogTrace("    {TypeName}", typeName);
            }

            return method;
        }

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + methodName + "(" +
                                                               string.Join(", ", argTypes) + ")");
    }

    public static string? Il2CppStringToManaged(IntPtr il2CppString)
    {
        if (il2CppString == IntPtr.Zero) return null;

        var length = il2cpp_string_length(il2CppString);
        var chars = il2cpp_string_chars(il2CppString);

        return new string(chars, 0, length);
    }

    public static IntPtr ManagedStringToIl2Cpp(string? str)
    {
        if (str == null) return IntPtr.Zero;

        fixed (char* chars = str)
        {
            return il2cpp_string_new_utf16(chars, str.Length);
        }
    }

    public static IntPtr Il2CppObjectBaseToPtr(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? IntPtr.Zero;
    }

    public static IntPtr Il2CppObjectBaseToPtrNotNull(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? throw new NullReferenceException();
    }

    public static IntPtr GetIl2CppNestedType(IntPtr enclosingType, string nestedTypeName)
    {
        if (enclosingType == IntPtr.Zero) return IntPtr.Zero;

        var iter = IntPtr.Zero;
        IntPtr nestedTypePtr;
        if (il2cpp_class_is_inflated(enclosingType))
        {
            Logger.Instance.LogTrace("Original class was inflated, falling back to reflection");

            return RuntimeReflectionHelper.GetNestedTypeViaReflection(enclosingType, nestedTypeName);
        }

        while ((nestedTypePtr = il2cpp_class_get_nested_types(enclosingType, ref iter)) != IntPtr.Zero)
            if (Marshal.PtrToStringAnsi(il2cpp_class_get_name(nestedTypePtr)) == nestedTypeName)
                return nestedTypePtr;

        Logger.Instance.LogError(
            "Nested type {NestedTypeName} on {EnclosingTypeName} not found!", nestedTypeName, Marshal.PtrToStringUTF8(il2cpp_class_get_name(enclosingType)));

        return IntPtr.Zero;
    }

    public static void ThrowIfNull(object arg)
    {
        if (arg == null)
            throw new NullReferenceException();
    }

    public static T ResolveICall<T>(string signature) where T : Delegate
    {
        var icallPtr = il2cpp_resolve_icall(signature);
        if (icallPtr == IntPtr.Zero)
        {
            Logger.Instance.LogTrace("ICall {Signature} not resolved", signature);
            return GenerateDelegateForMissingICall<T>(signature);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(icallPtr);
    }

    private static T GenerateDelegateForMissingICall<T>(string signature) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;

        var trampoline = new DynamicMethod("(missing icall delegate) " + typeof(T).FullName,
            invoke.ReturnType, invoke.GetParameters().Select(it => it.ParameterType).ToArray(), typeof(IL2CPP), true);
        var bodyBuilder = trampoline.GetILGenerator();

        bodyBuilder.Emit(OpCodes.Ldstr, $"ICall with signature {signature} was not resolved");
        bodyBuilder.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(new[] { typeof(string) })!);
        bodyBuilder.Emit(OpCodes.Throw);

        return (T)trampoline.CreateDelegate(typeof(T));
    }

    public static T? PointerToValueGeneric<T>(IntPtr objectPointer, bool isFieldPointer, bool valueTypeWouldBeBoxed)
    {
        if (isFieldPointer)
        {
            if (il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
                objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);
            else
                objectPointer = *(IntPtr*)objectPointer;
        }

        if (!valueTypeWouldBeBoxed && il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
            objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);

        if (typeof(T) == typeof(string))
            return (T)(object)Il2CppStringToManaged(objectPointer);

        if (objectPointer == IntPtr.Zero)
            return default;

        if (typeof(T).IsValueType)
            return Il2CppObjectBase.UnboxUnsafe<T>(objectPointer);

        return Il2CppObjectPool.Get<T>(objectPointer);
    }

    public static string RenderTypeName<T>(bool addRefMarker = false)
    {
        return RenderTypeName(typeof(T), addRefMarker);
    }

    public static string RenderTypeName(Type t, bool addRefMarker = false)
    {
        if (addRefMarker) return RenderTypeName(t) + "&";
        if (t.IsArray) return RenderTypeName(t.GetElementType()) + "[]";
        if (t.IsByRef) return RenderTypeName(t.GetElementType()) + "&";
        if (t.IsPointer) return RenderTypeName(t.GetElementType()) + "*";
        if (t.IsGenericParameter) return t.Name;

        if (t.IsGenericType)
        {
            if (t.TypeHasIl2CppArrayBase())
                return RenderTypeName(t.GetGenericArguments()[0]) + "[]";

            var builder = new StringBuilder();
            builder.Append(t.GetGenericTypeDefinition().FullNameObfuscated().TrimIl2CppPrefix());
            builder.Append('<');
            var genericArguments = t.GetGenericArguments();
            for (var i = 0; i < genericArguments.Length; i++)
            {
                if (i != 0) builder.Append(',');
                builder.Append(RenderTypeName(genericArguments[i]));
            }

            builder.Append('>');
            return builder.ToString();
        }

        if (t == typeof(Il2CppStringArray))
            return "System.String[]";

        return t.FullNameObfuscated().TrimIl2CppPrefix();
    }

    private static string FullNameObfuscated(this Type t)
    {
        var obfuscatedNameAnnotations = t.GetCustomAttribute<ObfuscatedNameAttribute>();
        if (obfuscatedNameAnnotations == null) return t.FullName;
        return obfuscatedNameAnnotations.ObfuscatedName;
    }

    private static string TrimIl2CppPrefix(this string s)
    {
        return s.StartsWith("Il2Cpp") ? s.Substring("Il2Cpp".Length) : s;
    }

    private static bool TypeHasIl2CppArrayBase(this Type type)
    {
        if (type == null) return false;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        if (type == typeof(Il2CppArrayBase<>)) return true;
        return TypeHasIl2CppArrayBase(type.BaseType);
    }

    // this is called if there's no actual il2cpp_gc_wbarrier_set_field()
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FieldWriteWbarrierStub(IntPtr obj, IntPtr targetAddress, IntPtr value)
    {
        // ignore obj
        *(IntPtr*)targetAddress = value;
    }

    // IL2CPP Functions
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "GdnPlVZEdxf")]
    public static extern void il2cpp_init(IntPtr domain_name);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "xeESwBbuab_")]
    public static extern void il2cpp_init_utf16(IntPtr domain_name);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "nKyghkcwxJe")]
    public static extern void il2cpp_shutdown();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "XiKOzOdJsYE")]
    public static extern void il2cpp_set_config_dir(IntPtr config_path);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "AbJEjDq_zgF")]
    public static extern void il2cpp_set_data_dir(IntPtr data_path);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "JerpDnxOVno")]
    public static extern void il2cpp_set_temp_dir(IntPtr temp_path);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "NkyFkhKjXDP")]
    public static extern void il2cpp_set_commandline_arguments(int argc, IntPtr argv, IntPtr basedir);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "UfNXctJaZQO")]
    public static extern void il2cpp_set_commandline_arguments_utf16(int argc, IntPtr argv, IntPtr basedir);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "MJdRIVcUmWY")]
    public static extern void il2cpp_set_config_utf16(IntPtr executablePath);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "igjWeppGXcn")]
    public static extern void il2cpp_set_config(IntPtr executablePath);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "NPuDEZOXejP")]
    public static extern void il2cpp_set_memory_callbacks(IntPtr callbacks);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "NGbgctWOdID")]
    public static extern IntPtr il2cpp_get_corlib();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "kdoNiyv_Qhd")]
    public static extern void il2cpp_add_internal_call(IntPtr name, IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "UWZdnspJd_I")]
    public static extern IntPtr il2cpp_resolve_icall([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "KaCKWSaBPCP")]
    public static extern IntPtr il2cpp_alloc(uint size);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "PRfrmVSTTrU")]
    public static extern void il2cpp_free(IntPtr ptr);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "LAiuXZUDdNh")]
    public static extern IntPtr il2cpp_array_class_get(IntPtr element_class, uint rank);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "QRIjzueyGZT")]
    public static extern uint il2cpp_array_length(IntPtr array);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "jzmYhGGuWvJ")]
    public static extern uint il2cpp_array_get_byte_length(IntPtr array);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "Nlz_loEWfav")]
    public static extern IntPtr il2cpp_array_new(IntPtr elementTypeInfo, ulong length);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "KBFdoRXhdON")]
    public static extern IntPtr il2cpp_array_new_specific(IntPtr arrayTypeInfo, ulong length);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "cJrJgWxPWSl")]
    public static extern IntPtr il2cpp_array_new_full(IntPtr array_class, ref ulong lengths, ref ulong lower_bounds);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "fyViQqbTTPe")]
    public static extern IntPtr il2cpp_bounded_array_class_get(IntPtr element_class, uint rank,
        [MarshalAs(UnmanagedType.I1)] bool bounded);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "rYLHFyGgORz")]
    public static extern int il2cpp_array_element_size(IntPtr array_class);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WrPjnXSxwvx")]
    public static extern IntPtr il2cpp_assembly_get_image(IntPtr assembly);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "GgOmkULknVl")]
    public static extern IntPtr il2cpp_class_enum_basetype(IntPtr klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "zLPRtXMqDka")]
    public static extern bool il2cpp_class_is_generic(IntPtr klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "vkWLZpdNfvo")]
    public static extern bool il2cpp_class_is_inflated(IntPtr klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "XkjrtGAMQyu")]
    public static extern bool il2cpp_class_is_assignable_from(IntPtr klass, IntPtr oklass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "bXykbnZeM__")]
    public static extern bool il2cpp_class_is_subclass_of(IntPtr klass, IntPtr klassc,
        [MarshalAs(UnmanagedType.I1)] bool check_interfaces);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "rFwPWfVVfiU")]
    public static extern bool il2cpp_class_has_parent(IntPtr klass, IntPtr klassc);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "OCipriHLgON")]
    public static extern IntPtr il2cpp_class_from_il2cpp_type(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "lQyvUscAGEB")]
    public static extern IntPtr il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaze,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "UgeTybrtfli")]
    public static extern IntPtr il2cpp_class_from_system_type(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "rLtGEybL_mo")]
    public static extern IntPtr il2cpp_class_get_element_class(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WYodECZGcFP")]
    public static extern IntPtr il2cpp_class_get_events(IntPtr klass, ref IntPtr iter);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "QZg_eWEoySR")]
    public static extern IntPtr il2cpp_class_get_fields(IntPtr klass, ref IntPtr iter);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "gcfwVglwRWR")]
    public static extern IntPtr il2cpp_class_get_nested_types(IntPtr klass, ref IntPtr iter);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "wfeudlBuJmW")]
    public static extern IntPtr il2cpp_class_get_interfaces(IntPtr klass, ref IntPtr iter);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ONYXmIhnfVE")]
    public static extern IntPtr il2cpp_class_get_properties(IntPtr klass, ref IntPtr iter);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "fHtXWY_hYew")]
    public static extern IntPtr il2cpp_class_get_property_from_name(IntPtr klass, IntPtr name);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "pwnfMkbaSCR")]
    public static extern IntPtr il2cpp_class_get_field_from_name(IntPtr klass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "XnXhgwwvBVH")]
    public static extern IntPtr il2cpp_class_get_methods(IntPtr klass, ref IntPtr iter);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "LmZVehSCtkU")]
    public static extern IntPtr il2cpp_class_get_method_from_name(IntPtr klass,
        [MarshalAs(UnmanagedType.LPStr)] string name, int argsCount);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "dswOlGpdlQB")]
    public static extern IntPtr il2cpp_class_get_name(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WBDCidKvO_s")]
    public static extern IntPtr il2cpp_class_get_namespace(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WnhlxMYyqXH")]
    public static extern IntPtr il2cpp_class_get_parent(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WrizOXPSfDX")]
    public static extern IntPtr il2cpp_class_get_declaring_type(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "cDrnGzyYYCV")]
    public static extern int il2cpp_class_instance_size(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "yZLIuPWSRcp")]
    public static extern uint il2cpp_class_num_fields(IntPtr enumKlass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "NjDEOSOBNto")]
    public static extern bool il2cpp_class_is_valuetype(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "RtljfJxmxEU")]
    public static extern int il2cpp_class_value_size(IntPtr klass, ref uint align);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "HXApjJi_zkt")]
    public static extern bool il2cpp_class_is_blittable(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "PfeRozEYgvw")]
    public static extern int il2cpp_class_get_flags(IntPtr klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WzhlplRFtZf")]
    public static extern bool il2cpp_class_is_abstract(IntPtr klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WssGtOLCvEB")]
    public static extern bool il2cpp_class_is_interface(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "EumfCrOhMGs")]
    public static extern int il2cpp_class_array_element_size(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "smBQHSuYocJ")]
    public static extern IntPtr il2cpp_class_from_type(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "iFUPHlNvxHm")]
    public static extern IntPtr il2cpp_class_get_type(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "gKusixTMGyH")]
    public static extern uint il2cpp_class_get_type_token(IntPtr klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "NvlrWGlVUQv")]
    public static extern bool il2cpp_class_has_attribute(IntPtr klass, IntPtr attr_class);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "sXmkDWLesBx")]
    public static extern bool il2cpp_class_has_references(IntPtr klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "UlxOBFbttaI")]
    public static extern bool il2cpp_class_is_enum(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "DhWncAFKTmf")]
    public static extern IntPtr il2cpp_class_get_image(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "utbv_l_vaJh")]
    public static extern IntPtr il2cpp_class_get_assemblyname(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "YlMdMSGIETk")]
    public static extern int il2cpp_class_get_rank(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WkzPjgTvSyM")]
    public static extern uint il2cpp_class_get_bitmap_size(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "VhiRbDWWYeu")]
    public static extern void il2cpp_class_get_bitmap(IntPtr klass, ref uint bitmap);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "qxUbeLx_JRW")]
    public static extern bool il2cpp_stats_dump_to_file(IntPtr path);

    //[DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    //public extern static ulong il2cpp_stats_get_value(IL2CPP_Stat stat);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "dUunpQADaAk")]
    public static extern IntPtr il2cpp_domain_get();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "bzhujLBvtDQ")]
    public static extern IntPtr il2cpp_domain_assembly_open(IntPtr domain, IntPtr name);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "kSNWlvRcuDN")]
    public static extern IntPtr* il2cpp_domain_get_assemblies(IntPtr domain, ref uint size);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr
        il2cpp_exception_from_name_msg(IntPtr image, IntPtr name_space, IntPtr name, IntPtr msg);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "fOphHxtGNbi")]
    public static extern IntPtr il2cpp_get_exception_argument_null(IntPtr arg);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "PWiBDLAcqEE")]
    public static extern void il2cpp_format_exception(IntPtr ex, void* message, int message_size);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "hYTrHKAtiVN")]
    public static extern void il2cpp_format_stack_trace(IntPtr ex, void* output, int output_size);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "yDsipRDieRK")]
    public static extern void il2cpp_unhandled_exception(IntPtr ex);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "kPkuadGKLtY")]
    public static extern int il2cpp_field_get_flags(IntPtr field);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "YsrafFiNMPb")]
    public static extern IntPtr il2cpp_field_get_name(IntPtr field);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "SCpPaWMGYuU")]
    public static extern IntPtr il2cpp_field_get_parent(IntPtr field);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "YnxEeoyzDsw")]
    public static extern uint il2cpp_field_get_offset(IntPtr field);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "vrXvLlMNtUF")]
    public static extern IntPtr il2cpp_field_get_type(IntPtr field);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "jkItflHvEgX")]
    public static extern void il2cpp_field_get_value(IntPtr obj, IntPtr field, void* value);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "nTqDKByJShq")]
    public static extern IntPtr il2cpp_field_get_value_object(IntPtr field, IntPtr obj);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "GBoRqEfHSur")]
    public static extern bool il2cpp_field_has_attribute(IntPtr field, IntPtr attr_class);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "uEATVRQMULc")]
    public static extern void il2cpp_field_set_value(IntPtr obj, IntPtr field, void* value);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "iL_PocEDOUL")]
    public static extern void il2cpp_field_static_get_value(IntPtr field, void* value);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "bWETBqDYXSG")]
    public static extern void il2cpp_field_static_set_value(IntPtr field, void* value);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "EZWtZRckXge")]
    public static extern void il2cpp_field_set_value_object(IntPtr instance, IntPtr field, IntPtr value);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "cxFieV_KvfU")]
    public static extern void il2cpp_gc_collect(int maxGenerations);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FLnvWMirTvD")]
    public static extern int il2cpp_gc_collect_a_little();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "OrADuiKfRaV")]
    public static extern void il2cpp_gc_disable();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "OnAc_HRKlnn")]
    public static extern void il2cpp_gc_enable();

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "CGhT_qkYMhz")]
    public static extern bool il2cpp_gc_is_disabled();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "_jtTAs_IieI")]
    public static extern long il2cpp_gc_get_used_size();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "jlxgOdsyfel")]
    public static extern long il2cpp_gc_get_heap_size();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "DDuUjGcKUaj")]
    public static extern void il2cpp_gc_wbarrier_set_field(IntPtr obj, IntPtr targetAddress, IntPtr gcObj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "HRWlelkrVPt")]
    public static extern nint il2cpp_gchandle_new(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mZCAEcHuXyK")]
    public static extern nint il2cpp_gchandle_new_weakref(IntPtr obj,
        [MarshalAs(UnmanagedType.I1)] bool track_resurrection);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "TxCTtfvbDXh")]
    public static extern IntPtr il2cpp_gchandle_get_target(nint gchandle);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "bETTzCiChhf")]
    public static extern void il2cpp_gchandle_free(nint gchandle);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_unity_liveness_calculation_begin(IntPtr filter, int max_object_count,
        IntPtr callback, IntPtr userdata, IntPtr onWorldStarted, IntPtr onWorldStopped);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_unity_liveness_calculation_end(IntPtr state);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "fxW_ygbYUpI")]
    public static extern void il2cpp_unity_liveness_calculation_from_root(IntPtr root, IntPtr state);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "cmnrTHTRjeg")]
    public static extern void il2cpp_unity_liveness_calculation_from_statics(IntPtr state);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "iOGphJxpUYi")]
    public static extern IntPtr il2cpp_method_get_return_type(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "SEwCkKakqHn")]
    public static extern IntPtr il2cpp_method_get_declaring_type(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "jgmSLpUuObu")]
    public static extern IntPtr il2cpp_method_get_name(IntPtr method);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntPtr il2cpp_method_get_from_reflection(IntPtr method)
    {
        if (UnityVersionHandler.HasGetMethodFromReflection) return _il2cpp_method_get_from_reflection(method);
        Il2CppReflectionMethod* reflectionMethod = (Il2CppReflectionMethod*)method;
        return (IntPtr)reflectionMethod->method;
    }
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "bY_ngrNPSBp")]
    private static extern IntPtr _il2cpp_method_get_from_reflection(IntPtr method);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "uJokkJUjpKT")]
    public static extern IntPtr il2cpp_method_get_object(IntPtr method, IntPtr refclass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "RaDdigmblJd")]
    public static extern bool il2cpp_method_is_generic(IntPtr method);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "MHOEcxWMXnX")]
    public static extern bool il2cpp_method_is_inflated(IntPtr method);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FYYVhKttIeM")]
    public static extern bool il2cpp_method_is_instance(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "SSrrPJtgRsy")]
    public static extern uint il2cpp_method_get_param_count(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "OiCJwDuUwiM")]
    public static extern IntPtr il2cpp_method_get_param(IntPtr method, uint index);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "gOZrNrFqRag")]
    public static extern IntPtr il2cpp_method_get_class(IntPtr method);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ZXROjjfaFd_")]
    public static extern bool il2cpp_method_has_attribute(IntPtr method, IntPtr attr_class);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "niJNnKvNPvP")]
    public static extern uint il2cpp_method_get_flags(IntPtr method, ref uint iflags);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "YMPcFDDspTK")]
    public static extern uint il2cpp_method_get_token(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "TH_aVezXhJW")]
    public static extern IntPtr il2cpp_method_get_param_name(IntPtr method, uint index);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install(IntPtr prof, IntPtr shutdown_callback);

    // [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    // public extern static void il2cpp_profiler_set_events(IL2CPP_ProfileFlags events);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_enter_leave(IntPtr enter, IntPtr fleave);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_allocation(IntPtr callback);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_gc(IntPtr callback, IntPtr heap_resize_callback);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_fileio(IntPtr callback);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_thread(IntPtr start, IntPtr end);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "MIEDlUbeuMx")]
    public static extern uint il2cpp_property_get_flags(IntPtr prop);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "CStyEE_NTSO")]
    public static extern IntPtr il2cpp_property_get_get_method(IntPtr prop);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ErevOpxaNmP")]
    public static extern IntPtr il2cpp_property_get_set_method(IntPtr prop);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ZQdUTOLZwVf")]
    public static extern IntPtr il2cpp_property_get_name(IntPtr prop);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "sbOVZuGEH_y")]
    public static extern IntPtr il2cpp_property_get_parent(IntPtr prop);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "qbFQR_fYihi")]
    public static extern IntPtr il2cpp_object_get_class(IntPtr obj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "dQqlXFwQrJp")]
    public static extern uint il2cpp_object_get_size(IntPtr obj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "uqHFVbHaEcE")]
    public static extern IntPtr il2cpp_object_get_virtual_method(IntPtr obj, IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "WCEoPQXKscc")]
    public static extern IntPtr il2cpp_object_new(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "syjaQBHbEba")]
    public static extern IntPtr il2cpp_object_unbox(IntPtr obj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "NrgBtNRZraW")]
    public static extern IntPtr il2cpp_value_box(IntPtr klass, IntPtr data);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "awVxxaDkin_")]
    public static extern void il2cpp_monitor_enter(IntPtr obj);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "BcAaeBubdxr")]
    public static extern bool il2cpp_monitor_try_enter(IntPtr obj, uint timeout);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "CKZFssaXtSR")]
    public static extern void il2cpp_monitor_exit(IntPtr obj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "BuJvkFvGnJJ")]
    public static extern void il2cpp_monitor_pulse(IntPtr obj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "XSZeuqqCyAD")]
    public static extern void il2cpp_monitor_pulse_all(IntPtr obj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "nNNrsaoljaS")]
    public static extern void il2cpp_monitor_wait(IntPtr obj);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mDSBbarFRGX")]
    public static extern bool il2cpp_monitor_try_wait(IntPtr obj, uint timeout);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "KsaFALsaHZl")]
    public static extern IntPtr il2cpp_runtime_invoke(IntPtr method, IntPtr obj, void** param, ref IntPtr exc);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "CcKieJOuNhD")]
    // param can be of Il2CppObject*
    public static extern IntPtr il2cpp_runtime_invoke_convert_args(IntPtr method, IntPtr obj, void** param,
        int paramCount, ref IntPtr exc);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "HpawpbBmsnf")]
    public static extern void il2cpp_runtime_class_init(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "JEyEEDZogZe")]
    public static extern void il2cpp_runtime_object_init(IntPtr obj);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "kFJQOElRJQz")]
    public static extern void il2cpp_runtime_object_init_exception(IntPtr obj, ref IntPtr exc);

    // [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    // public extern static void il2cpp_runtime_unhandled_exception_policy_set(IL2CPP_RuntimeUnhandledExceptionPolicy value);
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "DDuIaqSaJPb")]
    public static extern int il2cpp_string_length(IntPtr str);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "CAqjfDvGfOU")]
    public static extern char* il2cpp_string_chars(IntPtr str);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "moZkrEOxnod")]
    public static extern IntPtr il2cpp_string_new(string str);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "HiNmwaRQnUU")]
    public static extern IntPtr il2cpp_string_new_len(string str, uint length);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FQBIXIelhkZ")]
    public static extern IntPtr il2cpp_string_new_utf16(char* text, int len);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "sodVkROQWPR")]
    public static extern IntPtr il2cpp_string_new_wrapper(string str);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "browFOTyQoT")]
    public static extern IntPtr il2cpp_string_intern(string str);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "TmLQdbMHyHl")]
    public static extern IntPtr il2cpp_string_is_interned(string str);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "XHlseHzpXli")]
    public static extern IntPtr il2cpp_thread_current();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "yXKCkTHabfE")]
    public static extern IntPtr il2cpp_thread_attach(IntPtr domain);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mUkuUnAcYiz")]
    public static extern void il2cpp_thread_detach(IntPtr thread);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "rpORpEUDUUG")]
    public static extern void** il2cpp_thread_get_all_attached_threads(ref uint size);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "YTwnbYaBvtF")]
    public static extern bool il2cpp_is_vm_thread(IntPtr thread);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "_kIiiFONOWj")]
    public static extern void il2cpp_current_thread_walk_frame_stack(IntPtr func, IntPtr user_data);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "txCdvPkSQ_U")]
    public static extern void il2cpp_thread_walk_frame_stack(IntPtr thread, IntPtr func, IntPtr user_data);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "aBVnkQmVagn")]
    public static extern bool il2cpp_current_thread_get_top_frame(IntPtr frame);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "QfgAeisWpKk")]
    public static extern bool il2cpp_thread_get_top_frame(IntPtr thread, IntPtr frame);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ljruylFjhzQ")]
    public static extern bool il2cpp_current_thread_get_frame_at(int offset, IntPtr frame);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "vfdtQi_BLnN")]
    public static extern bool il2cpp_thread_get_frame_at(IntPtr thread, int offset, IntPtr frame);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "_jfwrZMaxrS")]
    public static extern int il2cpp_current_thread_get_stack_depth();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "zxvFVDsgBzv")]
    public static extern int il2cpp_thread_get_stack_depth(IntPtr thread);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "tRAcfCA_cJ_")]
    public static extern IntPtr il2cpp_type_get_object(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "YqifqTyuPPh")]
    public static extern int il2cpp_type_get_type(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "T__hQLBTfcK")]
    public static extern IntPtr il2cpp_type_get_class_or_element_class(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "hLEJNjZDdWu")]
    public static extern IntPtr il2cpp_type_get_name(IntPtr type);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "CLNqWpxdFVp")]
    public static extern bool il2cpp_type_is_byref(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "Uz_cHOJRUgo")]
    public static extern uint il2cpp_type_get_attrs(IntPtr type);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "AridOgtMmI_")]
    public static extern bool il2cpp_type_equals(IntPtr type, IntPtr otherType);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "rrU_vIEwXbL")]
    public static extern IntPtr il2cpp_type_get_assembly_qualified_name(IntPtr type);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "BNuYOmYhPYR")]
    public static extern IntPtr il2cpp_image_get_assembly(IntPtr image);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ikzAkyTEACE")]
    public static extern IntPtr il2cpp_image_get_name(IntPtr image);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "oZQRiHguhsf")]
    public static extern IntPtr il2cpp_image_get_filename(IntPtr image);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "btgtJbGbhiv")]
    public static extern IntPtr il2cpp_image_get_entry_point(IntPtr image);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "CQzpQfpTRri")]
    public static extern uint il2cpp_image_get_class_count(IntPtr image);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "zUdXVkYrdqv")]
    public static extern IntPtr il2cpp_image_get_class(IntPtr image, uint index);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mplYajYvgG_")]
    public static extern IntPtr il2cpp_capture_memory_snapshot();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "EFaaD_TxFmT")]
    public static extern void il2cpp_free_captured_memory_snapshot(IntPtr snapshot);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mpxpzMXGXjG")]
    public static extern void il2cpp_set_find_plugin_callback(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "oHdI_cT_Dzm")]
    public static extern void il2cpp_register_log_callback(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "yTOgoQPzSCr")]
    public static extern void il2cpp_debugger_set_agent_options(IntPtr options);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "hJkADbjlltQ")]
    public static extern bool il2cpp_is_debugger_attached();

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "JigYUjFcHix")]
    public static extern void il2cpp_unity_install_unitytls_interface(void* unitytlsInterfaceStruct);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "wDJQiJNSxdU")]
    public static extern IntPtr il2cpp_custom_attrs_from_class(IntPtr klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "hdgWuNBjZHE")]
    public static extern IntPtr il2cpp_custom_attrs_from_method(IntPtr method);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "jtFcNDldAcC")]
    public static extern IntPtr il2cpp_custom_attrs_get_attr(IntPtr ainfo, IntPtr attr_klass);

    [return: MarshalAs(UnmanagedType.I1)]
    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "iFJnoOxafey")]
    public static extern bool il2cpp_custom_attrs_has_attr(IntPtr ainfo, IntPtr attr_klass);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "KPoSbzPCuBP")]
    public static extern IntPtr il2cpp_custom_attrs_construct(IntPtr cinfo);

    [DllImport("libil2cpp.so", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "YMpwqonKaMS")]
    public static extern void il2cpp_custom_attrs_free(IntPtr ainfo);
}
