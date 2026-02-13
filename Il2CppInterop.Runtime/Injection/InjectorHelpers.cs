using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Extensions;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Injection.Hooks;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Assembly;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.FieldInfo;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Image;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Il2CppInterop.Runtime.Startup;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection
{
    internal static unsafe class InjectorHelpers
    {
        internal static Assembly Il2CppMscorlib = typeof(Il2CppSystem.Type).Assembly;
        internal static INativeAssemblyStruct InjectedAssembly;
        internal static INativeImageStruct InjectedImage;
        internal static ProcessModule Il2CppModule = Process.GetCurrentProcess()
            .Modules.OfType<ProcessModule>()
            .Last((x) => x.ModuleName is "GameAssembly.dll" or "GameAssembly.so" or "UserAssembly.dll" or "libil2cpp.so");

        internal static IntPtr Il2CppHandle = NativeLibrary.Load("libil2cpp.so", typeof(InjectorHelpers).Assembly, null);

        internal static readonly Dictionary<Type, OpCode> StIndOpcodes = new()
        {
            [typeof(byte)] = OpCodes.Stind_I1,
            [typeof(sbyte)] = OpCodes.Stind_I1,
            [typeof(bool)] = OpCodes.Stind_I1,
            [typeof(short)] = OpCodes.Stind_I2,
            [typeof(ushort)] = OpCodes.Stind_I2,
            [typeof(int)] = OpCodes.Stind_I4,
            [typeof(uint)] = OpCodes.Stind_I4,
            [typeof(long)] = OpCodes.Stind_I8,
            [typeof(ulong)] = OpCodes.Stind_I8,
            [typeof(float)] = OpCodes.Stind_R4,
            [typeof(double)] = OpCodes.Stind_R8
        };

        private static void CreateInjectedAssembly()
        {
            InjectedAssembly = UnityVersionHandler.NewAssembly();
            InjectedImage = UnityVersionHandler.NewImage();

            InjectedAssembly.Name.Name = Marshal.StringToHGlobalAnsi("InjectedMonoTypes");

            InjectedImage.Assembly = InjectedAssembly.AssemblyPointer;
            InjectedImage.Dynamic = 1;
            InjectedImage.Name = InjectedAssembly.Name.Name;
            if (InjectedImage.HasNameNoExt)
                InjectedImage.NameNoExt = InjectedAssembly.Name.Name;
        }

        private static readonly GenericMethod_GetMethod_Hook GenericMethodGetMethodHook = new();
        private static readonly MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook GetTypeInfoFromTypeDefinitionIndexHook = new();
        private static readonly Class_GetFieldDefaultValue_Hook GetFieldDefaultValueHook = new();
        private static readonly Class_FromIl2CppType_Hook FromIl2CppTypeHook = new();
        private static readonly Class_FromName_Hook FromNameHook = new();
        private static readonly GarbageCollector_RunFinalizer_Patch RunFinalizerPatch = new();

        internal static void Setup()
        {
            if (InjectedAssembly == null) CreateInjectedAssembly();
            GenericMethodGetMethodHook.ApplyHook();
            //GetTypeInfoFromTypeDefinitionIndexHook.ApplyHook();
            GetFieldDefaultValueHook.ApplyHook();
            ClassInit ??= FindClassInit();
            FromIl2CppTypeHook.ApplyHook();
            FromNameHook.ApplyHook();
            RunFinalizerPatch.ApplyHook();
        }

        internal static long CreateClassToken(IntPtr classPointer)
        {
            long newToken = Interlocked.Decrement(ref s_LastInjectedToken);
            s_InjectedClasses[newToken] = classPointer;
            return newToken;
        }

        internal static void AddTypeToLookup<T>(IntPtr typePointer) where T : class => AddTypeToLookup(typeof(T), typePointer);
        internal static void AddTypeToLookup(Type type, IntPtr typePointer)
        {
            string klass = type.Name;
            if (klass == null) return;
            string namespaze = type.Namespace ?? string.Empty;
            var attribute = Attribute.GetCustomAttribute(type, typeof(Il2CppInterop.Runtime.Attributes.ClassInjectionAssemblyTargetAttribute)) as Il2CppInterop.Runtime.Attributes.ClassInjectionAssemblyTargetAttribute;

            foreach (IntPtr image in (attribute is null) ? IL2CPP.GetIl2CppImages() : attribute.GetImagePointers())
            {
                s_ClassNameLookup.Add((namespaze, klass, image), typePointer);
            }
        }

        internal static IntPtr GetIl2CppExport(string name)
        {
            if (!TryGetIl2CppExport(name, out var address))
            {
                throw new NotSupportedException($"Couldn't find {name} in {Il2CppModule.ModuleName}'s exports");
            }

            return address;
        }

        static Dictionary<string, string> Exports = new() { { "il2cpp_init", "GdnPlVZEdxf" }, { "il2cpp_init_utf16", "xeESwBbuab_" }, { "il2cpp_shutdown", "nKyghkcwxJe" }, { "il2cpp_set_config_dir", "XiKOzOdJsYE" }, { "il2cpp_set_data_dir", "AbJEjDq_zgF" }, { "il2cpp_set_temp_dir", "JerpDnxOVno" }, { "il2cpp_set_commandline_arguments", "NkyFkhKjXDP" }, { "il2cpp_set_commandline_arguments_utf16", "UfNXctJaZQO" }, { "il2cpp_set_config_utf16", "MJdRIVcUmWY" }, { "il2cpp_set_config", "igjWeppGXcn" }, { "il2cpp_set_memory_callbacks", "NPuDEZOXejP" }, { "il2cpp_memory_pool_set_region_size", "EAFNJ_FIJYK" }, { "il2cpp_memory_pool_get_region_size", "gdwVBiglIWC" }, { "il2cpp_get_corlib", "NGbgctWOdID" }, { "il2cpp_add_internal_call", "kdoNiyv_Qhd" }, { "il2cpp_resolve_icall", "UWZdnspJd_I" }, { "il2cpp_alloc", "KaCKWSaBPCP" }, { "il2cpp_free", "PRfrmVSTTrU" }, { "il2cpp_array_class_get", "LAiuXZUDdNh" }, { "il2cpp_array_length", "QRIjzueyGZT" }, { "il2cpp_array_get_byte_length", "jzmYhGGuWvJ" }, { "il2cpp_array_new", "Nlz_loEWfav" }, { "il2cpp_array_new_specific", "KBFdoRXhdON" }, { "il2cpp_array_new_full", "cJrJgWxPWSl" }, { "il2cpp_bounded_array_class_get", "fyViQqbTTPe" }, { "il2cpp_array_element_size", "rYLHFyGgORz" }, { "il2cpp_assembly_get_image", "WrPjnXSxwvx" }, { "il2cpp_class_for_each", "bU_ybtImqeh" }, { "il2cpp_class_enum_basetype", "GgOmkULknVl" }, { "il2cpp_class_is_inited", "gnQolWebGMV" }, { "il2cpp_class_is_generic", "zLPRtXMqDka" }, { "il2cpp_class_is_inflated", "vkWLZpdNfvo" }, { "il2cpp_class_is_assignable_from", "XkjrtGAMQyu" }, { "il2cpp_class_is_subclass_of", "bXykbnZeM__" }, { "il2cpp_class_has_parent", "rFwPWfVVfiU" }, { "il2cpp_class_from_il2cpp_type", "OCipriHLgON" }, { "il2cpp_class_from_name", "lQyvUscAGEB" }, { "il2cpp_class_from_system_type", "UgeTybrtfli" }, { "il2cpp_class_get_element_class", "rLtGEybL_mo" }, { "il2cpp_class_get_events", "WYodECZGcFP" }, { "il2cpp_class_get_fields", "QZg_eWEoySR" }, { "il2cpp_class_get_nested_types", "gcfwVglwRWR" }, { "il2cpp_class_get_interfaces", "wfeudlBuJmW" }, { "il2cpp_class_get_properties", "ONYXmIhnfVE" }, { "il2cpp_class_get_property_from_name", "fHtXWY_hYew" }, { "il2cpp_class_get_field_from_name", "pwnfMkbaSCR" }, { "il2cpp_class_get_methods", "XnXhgwwvBVH" }, { "il2cpp_class_get_method_from_name", "LmZVehSCtkU" }, { "il2cpp_class_get_name", "dswOlGpdlQB" }, { "il2cpp_type_get_name_chunked", "XvW_nQkLeKA" }, { "il2cpp_class_get_namespace", "WBDCidKvO_s" }, { "il2cpp_class_get_parent", "WnhlxMYyqXH" }, { "il2cpp_class_get_declaring_type", "WrizOXPSfDX" }, { "il2cpp_class_instance_size", "cDrnGzyYYCV" }, { "il2cpp_class_num_fields", "yZLIuPWSRcp" }, { "il2cpp_class_is_valuetype", "NjDEOSOBNto" }, { "il2cpp_class_value_size", "RtljfJxmxEU" }, { "il2cpp_class_is_blittable", "HXApjJi_zkt" }, { "il2cpp_class_get_flags", "PfeRozEYgvw" }, { "il2cpp_class_is_abstract", "WzhlplRFtZf" }, { "il2cpp_class_is_interface", "WssGtOLCvEB" }, { "il2cpp_class_array_element_size", "EumfCrOhMGs" }, { "il2cpp_class_from_type", "smBQHSuYocJ" }, { "il2cpp_class_get_type", "iFUPHlNvxHm" }, { "il2cpp_class_get_type_token", "gKusixTMGyH" }, { "il2cpp_class_has_attribute", "NvlrWGlVUQv" }, { "il2cpp_class_has_references", "sXmkDWLesBx" }, { "il2cpp_class_is_enum", "UlxOBFbttaI" }, { "il2cpp_class_get_image", "DhWncAFKTmf" }, { "il2cpp_class_get_assemblyname", "utbv_l_vaJh" }, { "il2cpp_class_get_rank", "YlMdMSGIETk" }, { "il2cpp_class_get_data_size", "eHFwUICBnYH" }, { "il2cpp_class_get_static_field_data", "aNHKESETrgt" }, { "il2cpp_class_get_bitmap_size", "WkzPjgTvSyM" }, { "il2cpp_class_get_bitmap", "VhiRbDWWYeu" }, { "il2cpp_stats_dump_to_file", "qxUbeLx_JRW" }, { "il2cpp_stats_get_value", "mpPYSsTepIF" }, { "il2cpp_domain_get", "dUunpQADaAk" }, { "il2cpp_domain_assembly_open", "bzhujLBvtDQ" }, { "il2cpp_domain_get_assemblies", "kSNWlvRcuDN" }, { "il2cpp_raise_exception", "YDFMwomdxHH" }, { "il2cpp_exception_from_name_msg", "lfkDsxPTfxx" }, { "il2cpp_get_exception_argument_null", "fOphHxtGNbi" }, { "il2cpp_format_exception", "PWiBDLAcqEE" }, { "il2cpp_format_stack_trace", "hYTrHKAtiVN" }, { "il2cpp_unhandled_exception", "yDsipRDieRK" }, { "il2cpp_native_stack_trace", "Tup_trWcPft" }, { "il2cpp_field_get_flags", "kPkuadGKLtY" }, { "il2cpp_field_get_name", "YsrafFiNMPb" }, { "il2cpp_field_get_parent", "SCpPaWMGYuU" }, { "il2cpp_field_get_offset", "YnxEeoyzDsw" }, { "il2cpp_field_get_type", "vrXvLlMNtUF" }, { "il2cpp_field_get_value", "jkItflHvEgX" }, { "il2cpp_field_get_value_object", "nTqDKByJShq" }, { "il2cpp_field_has_attribute", "GBoRqEfHSur" }, { "il2cpp_field_set_value", "uEATVRQMULc" }, { "il2cpp_field_static_get_value", "iL_PocEDOUL" }, { "il2cpp_field_static_set_value", "bWETBqDYXSG" }, { "il2cpp_field_set_value_object", "EZWtZRckXge" }, { "il2cpp_field_is_literal", "ZpVlhnPyPjF" }, { "il2cpp_gc_collect", "cxFieV_KvfU" }, { "il2cpp_gc_collect_a_little", "FLnvWMirTvD" }, { "il2cpp_gc_start_incremental_collection", "WsXbVfkwBFl" }, { "il2cpp_gc_disable", "OrADuiKfRaV" }, { "il2cpp_gc_enable", "OnAc_HRKlnn" }, { "il2cpp_gc_is_disabled", "CGhT_qkYMhz" }, { "il2cpp_gc_set_mode", "knvVyiMUlFH" }, { "il2cpp_gc_get_max_time_slice_ns", "xmHvAlsCgvg" }, { "il2cpp_gc_set_max_time_slice_ns", "IAHkloIMjeA" }, { "il2cpp_gc_is_incremental", "ezlpJ_GUuck" }, { "il2cpp_gc_get_used_size", "_jtTAs_IieI" }, { "il2cpp_gc_get_heap_size", "jlxgOdsyfel" }, { "il2cpp_gc_wbarrier_set_field", "DDuUjGcKUaj" }, { "il2cpp_gc_has_strict_wbarriers", "XIBLQ_EpJtY" }, { "il2cpp_gc_set_external_allocation_tracker", "YMqyW_Qt_Ld" }, { "il2cpp_gc_set_external_wbarrier_tracker", "yG_WSutBToN" }, { "il2cpp_gc_foreach_heap", "dNQnXjbmJKE" }, { "il2cpp_stop_gc_world", "mIgMYhkAJMu" }, { "il2cpp_start_gc_world", "pqnyVuh_ujd" }, { "il2cpp_gc_alloc_fixed", "iVJSVIQCwWl" }, { "il2cpp_gc_free_fixed", "xvhyUdIbVgy" }, { "il2cpp_gchandle_new", "HRWlelkrVPt" }, { "il2cpp_gchandle_new_weakref", "mZCAEcHuXyK" }, { "il2cpp_gchandle_get_target", "TxCTtfvbDXh" }, { "il2cpp_gchandle_free", "bETTzCiChhf" }, { "il2cpp_gchandle_foreach_get_target", "OhgKQzlOETr" }, { "il2cpp_object_header_size", "HJPxFieAabM" }, { "il2cpp_array_object_header_size", "KrZKSmKtPJy" }, { "il2cpp_offset_of_array_length_in_array_object_header", "ipNioOb_TTJ" }, { "il2cpp_offset_of_array_bounds_in_array_object_header", "OpdiQkwodGd" }, { "il2cpp_allocation_granularity", "jDHAPfbJGb_" }, { "il2cpp_unity_liveness_allocate_struct", "YxvMvG_EOgK" }, { "il2cpp_unity_liveness_calculation_from_root", "fxW_ygbYUpI" }, { "il2cpp_unity_liveness_calculation_from_statics", "cmnrTHTRjeg" }, { "il2cpp_unity_liveness_finalize", "ckudBVjX_tc" }, { "il2cpp_unity_liveness_free_struct", "rQ_xDeHwdph" }, { "il2cpp_method_get_return_type", "iOGphJxpUYi" }, { "il2cpp_method_get_declaring_type", "SEwCkKakqHn" }, { "il2cpp_method_get_name", "jgmSLpUuObu" }, { "il2cpp_method_get_from_reflection", "bY_ngrNPSBp" }, { "il2cpp_method_get_object", "uJokkJUjpKT" }, { "il2cpp_method_is_generic", "RaDdigmblJd" }, { "il2cpp_method_is_inflated", "MHOEcxWMXnX" }, { "il2cpp_method_is_instance", "FYYVhKttIeM" }, { "il2cpp_method_get_param_count", "SSrrPJtgRsy" }, { "il2cpp_method_get_param", "OiCJwDuUwiM" }, { "il2cpp_method_get_class", "gOZrNrFqRag" }, { "il2cpp_method_has_attribute", "ZXROjjfaFd_" }, { "il2cpp_method_get_flags", "niJNnKvNPvP" }, { "il2cpp_method_get_token", "YMPcFDDspTK" }, { "il2cpp_method_get_param_name", "TH_aVezXhJW" }, { "il2cpp_property_get_flags", "MIEDlUbeuMx" }, { "il2cpp_property_get_get_method", "CStyEE_NTSO" }, { "il2cpp_property_get_set_method", "ErevOpxaNmP" }, { "il2cpp_property_get_name", "ZQdUTOLZwVf" }, { "il2cpp_property_get_parent", "sbOVZuGEH_y" }, { "il2cpp_object_get_class", "qbFQR_fYihi" }, { "il2cpp_object_get_size", "dQqlXFwQrJp" }, { "il2cpp_object_get_virtual_method", "uqHFVbHaEcE" }, { "il2cpp_object_new", "WCEoPQXKscc" }, { "il2cpp_object_unbox", "syjaQBHbEba" }, { "il2cpp_value_box", "NrgBtNRZraW" }, { "il2cpp_monitor_enter", "awVxxaDkin_" }, { "il2cpp_monitor_try_enter", "BcAaeBubdxr" }, { "il2cpp_monitor_exit", "CKZFssaXtSR" }, { "il2cpp_monitor_pulse", "BuJvkFvGnJJ" }, { "il2cpp_monitor_pulse_all", "XSZeuqqCyAD" }, { "il2cpp_monitor_wait", "nNNrsaoljaS" }, { "il2cpp_monitor_try_wait", "mDSBbarFRGX" }, { "il2cpp_runtime_invoke", "KsaFALsaHZl" }, { "il2cpp_runtime_invoke_convert_args", "CcKieJOuNhD" }, { "il2cpp_runtime_class_init", "HpawpbBmsnf" }, { "il2cpp_runtime_object_init", "JEyEEDZogZe" }, { "il2cpp_runtime_object_init_exception", "kFJQOElRJQz" }, { "il2cpp_runtime_unhandled_exception_policy_set", "yuiRVqneHRq" }, { "il2cpp_string_length", "DDuIaqSaJPb" }, { "il2cpp_string_chars", "CAqjfDvGfOU" }, { "il2cpp_string_new", "moZkrEOxnod" }, { "il2cpp_string_new_len", "HiNmwaRQnUU" }, { "il2cpp_string_new_utf16", "FQBIXIelhkZ" }, { "il2cpp_string_new_wrapper", "sodVkROQWPR" }, { "il2cpp_string_intern", "browFOTyQoT" }, { "il2cpp_string_is_interned", "TmLQdbMHyHl" }, { "il2cpp_thread_current", "XHlseHzpXli" }, { "il2cpp_thread_attach", "yXKCkTHabfE" }, { "il2cpp_thread_detach", "mUkuUnAcYiz" }, { "il2cpp_thread_get_all_attached_threads", "rpORpEUDUUG" }, { "il2cpp_is_vm_thread", "YTwnbYaBvtF" }, { "il2cpp_current_thread_walk_frame_stack", "_kIiiFONOWj" }, { "il2cpp_thread_walk_frame_stack", "txCdvPkSQ_U" }, { "il2cpp_current_thread_get_top_frame", "aBVnkQmVagn" }, { "il2cpp_thread_get_top_frame", "QfgAeisWpKk" }, { "il2cpp_current_thread_get_frame_at", "ljruylFjhzQ" }, { "il2cpp_thread_get_frame_at", "vfdtQi_BLnN" }, { "il2cpp_current_thread_get_stack_depth", "_jfwrZMaxrS" }, { "il2cpp_thread_get_stack_depth", "zxvFVDsgBzv" }, { "il2cpp_override_stack_backtrace", "AgtoKcmHcDU" }, { "il2cpp_type_get_object", "tRAcfCA_cJ_" }, { "il2cpp_type_get_type", "YqifqTyuPPh" }, { "il2cpp_type_get_class_or_element_class", "T__hQLBTfcK" }, { "il2cpp_type_get_name", "hLEJNjZDdWu" }, { "il2cpp_type_is_byref", "CLNqWpxdFVp" }, { "il2cpp_type_get_attrs", "Uz_cHOJRUgo" }, { "il2cpp_type_equals", "AridOgtMmI_" }, { "il2cpp_type_get_assembly_qualified_name", "rrU_vIEwXbL" }, { "il2cpp_type_get_reflection_name", "snWGrBGxadY" }, { "il2cpp_type_is_static", "ffYPiNKgTWN" }, { "il2cpp_type_is_pointer_type", "BgeV_bMfWIG" }, { "il2cpp_image_get_assembly", "BNuYOmYhPYR" }, { "il2cpp_image_get_name", "ikzAkyTEACE" }, { "il2cpp_image_get_filename", "oZQRiHguhsf" }, { "il2cpp_image_get_entry_point", "btgtJbGbhiv" }, { "il2cpp_image_get_class_count", "CQzpQfpTRri" }, { "il2cpp_image_get_class", "zUdXVkYrdqv" }, { "il2cpp_capture_memory_snapshot", "mplYajYvgG_" }, { "il2cpp_free_captured_memory_snapshot", "EFaaD_TxFmT" }, { "il2cpp_set_find_plugin_callback", "mpxpzMXGXjG" }, { "il2cpp_register_log_callback", "oHdI_cT_Dzm" }, { "il2cpp_debugger_set_agent_options", "yTOgoQPzSCr" }, { "il2cpp_is_debugger_attached", "hJkADbjlltQ" }, { "il2cpp_register_debugger_agent_transport", "hKyInMXWsOU" }, { "il2cpp_debug_get_method_info", "FqpgSyKjrdo" }, { "il2cpp_unity_install_unitytls_interface", "JigYUjFcHix" }, { "il2cpp_custom_attrs_from_class", "wDJQiJNSxdU" }, { "il2cpp_custom_attrs_from_method", "hdgWuNBjZHE" }, { "il2cpp_custom_attrs_from_field", "TSNtIHFohaO" }, { "il2cpp_custom_attrs_get_attr", "jtFcNDldAcC" }, { "il2cpp_custom_attrs_has_attr", "iFJnoOxafey" }, { "il2cpp_custom_attrs_construct", "KPoSbzPCuBP" }, { "il2cpp_custom_attrs_free", "YMpwqonKaMS" }, { "il2cpp_class_set_userdata", "IN_yUMndXZh" }, { "il2cpp_class_get_userdata_offset", "iDJFVOADMyw" }, { "il2cpp_set_default_thread_affinity", "ygXP_vcjIjp" }, { "il2cpp_unity_set_android_network_up_state_func", "vq_ROHDlwlL" } };
        internal static bool TryGetIl2CppExport(string name, out IntPtr address)
        {
            if (Exports.TryGetValue(name, out string export))
            {
                name = export;
            }
            return NativeLibrary.TryGetExport(Il2CppHandle, name, out address);
        }

        internal static IntPtr GetIl2CppMethodPointer(MethodBase proxyMethod)
        {
            if (proxyMethod == null) return IntPtr.Zero;

            FieldInfo methodInfoPointerField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(proxyMethod);
            if (methodInfoPointerField == null)
                throw new ArgumentException($"Couldn't find the generated method info pointer for {proxyMethod.Name}");

            // Il2CppClassPointerStore calls the static constructor for the type
            Il2CppClassPointerStore.GetNativeClassPointer(proxyMethod.DeclaringType);

            IntPtr methodInfoPointer = (IntPtr)methodInfoPointerField.GetValue(null);
            if (methodInfoPointer == IntPtr.Zero)
                throw new ArgumentException($"Generated method info pointer for {proxyMethod.Name} doesn't point to any il2cpp method info");
            INativeMethodInfoStruct methodInfo = UnityVersionHandler.Wrap((Il2CppMethodInfo*)methodInfoPointer);
            return methodInfo.MethodPointer;
        }

        private static long s_LastInjectedToken = -2;
        internal static readonly ConcurrentDictionary<long, IntPtr> s_InjectedClasses = new();
        /// <summary> (namespace, class, image) : class </summary>
        internal static readonly Dictionary<(string _namespace, string _class, IntPtr imagePtr), IntPtr> s_ClassNameLookup = new();

        #region Class::Init
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void d_ClassInit(Il2CppClass* klass);
        internal static d_ClassInit ClassInit;

        private static d_ClassInit FindClassInit()
        {
            static nint GetClassInitSubstitute()
            {
                if (TryGetIl2CppExport(nameof(IL2CPP.il2cpp_array_new_specific), out nint arrayNewSpecific))
                {
                    // https://github.com/ByNameModding/BNM-Android/blob/3edeec43d74fc4392ba1b1eb9d5002e1b2ef2a67/src/Loading.cpp#L296
                    var bnmClassInit = XrefScannerLowLevel.JumpTargets(XrefScannerLowLevel.JumpTargets(arrayNewSpecific).First()).First();
                    if (bnmClassInit != IntPtr.Zero)
                    {
                        Logger.Instance.LogTrace("Used BNM Method to find Class::Init.");
                        return bnmClassInit;
                    }
                }
                if (TryGetIl2CppExport("mono_class_instance_size", out nint classInit))
                {
                    Logger.Instance.LogTrace("Picked mono_class_instance_size as a Class::Init substitute");
                    return classInit;
                }
                if (TryGetIl2CppExport("mono_class_setup_vtable", out classInit))
                {
                    Logger.Instance.LogTrace("Picked mono_class_setup_vtable as a Class::Init substitute");
                    return classInit;
                }
                if (TryGetIl2CppExport(nameof(IL2CPP.il2cpp_class_has_references), out classInit))
                {
                    Logger.Instance.LogTrace("Picked il2cpp_class_has_references as a Class::Init substitute");
                    return classInit;
                }

                Logger.Instance.LogTrace("GameAssembly.dll: 0x{Il2CppModuleAddress}", Il2CppModule.BaseAddress.ToInt64().ToString("X2"));
                throw new NotSupportedException("Failed to use signature for Class::Init and a substitute cannot be found, please create an issue and report your unity version & game");
            }
            nint pClassInit = GetClassInitSubstitute();

            Logger.Instance.LogTrace("Class::Init: 0x{PClassInitAddress}", pClassInit.ToString("X2"));

            return Marshal.GetDelegateForFunctionPointer<d_ClassInit>(pClassInit);
        }
        #endregion
    }
}
