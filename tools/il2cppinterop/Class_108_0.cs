using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Type;
namespace Il2CppInterop.Runtime.Runtime.VersionSpecific.Class
{
    // Unity 6000.6 (IL2CPP metadata v108). Layout recovered from GameAssembly exports
    // (il2cpp_class_* getters). Compared to 29_2, implementedInterfaces was merged into the
    // interfaceOffsets table (16-byte entries: Il2CppClass*, int32 offset, int32 flags) and
    // events/properties/nestedTypes moved after typeHierarchy.
    [ApplicableToUnityVersionsSince("6000.6.0")]
    public unsafe class NativeClassStructHandler_108_0 : INativeClassStructHandler
    {
        public int Size() => sizeof(Il2CppClass_108_0);
        public INativeClassStruct CreateNewStruct(int vTableSlots)
        {
            IntPtr ptr = Marshal.AllocHGlobal(Size() + sizeof(VirtualInvokeData) * vTableSlots);
            Il2CppClass_108_0* _ = (Il2CppClass_108_0*)ptr;
            *_ = default;
            return new NativeStructWrapper(ptr);
        }
        public INativeClassStruct Wrap(Il2CppClass* ptr)
        {
            if (ptr == null) return null;
            return new NativeStructWrapper((IntPtr)ptr);
        }
        internal unsafe struct Il2CppClass_108_0
        {
            public Il2CppImage* image;                                       // 0x00
            public void* gc_desc;                                            // 0x08
            public byte* name;                                               // 0x10
            public byte* namespaze;                                          // 0x18
            public NativeTypeStructHandler_27_0.Il2CppType_27_0 byval_arg;   // 0x20
            public NativeTypeStructHandler_27_0.Il2CppType_27_0 this_arg;    // 0x30
            public Il2CppClass* element_class;                               // 0x40
            public Il2CppClass* castClass;                                   // 0x48
            public Il2CppClass* declaringType;                               // 0x50
            public Il2CppClass* parent;                                      // 0x58
            public void* generic_class;                                      // 0x60
            public Il2CppMetadataTypeHandle typeMetadataHandle;              // 0x68
            public void* interopData;                                        // 0x70
            public Il2CppClass* klass;                                       // 0x78
            public Il2CppFieldInfo* fields;                                  // 0x80
            public Il2CppMethodInfo** methods;                               // 0x88
            public Il2CppRuntimeInterfaceOffsetPair* interfaceOffsets;       // 0x90
            public void* static_fields;                                      // 0x98
            public void* rgctx_data;                                         // 0xa0
            public Il2CppClass** typeHierarchy;                              // 0xa8
            public Il2CppEventInfo* events;                                  // 0xb0
            public Il2CppPropertyInfo* properties;                           // 0xb8
            public Il2CppClass** nestedTypes;                                // 0xc0
            public void* unity_user_data;                                    // 0xc8
            public Il2CppGCHandle initializationExceptionGCHandle;           // 0xd0
            public uint cctor_started;                                       // 0xd8
            public uint cctor_finished_or_no_cctor;                          // 0xdc
            public IntPtr cctor_thread;                                      // 0xe0
            public Il2CppMetadataGenericContainerHandle genericContainerHandle; // 0xe8
            public uint instance_size;                                       // 0xf0
            public uint stack_slot_size;
            public uint actualSize;
            public uint element_size;
            public int native_size;
            public uint static_fields_size;                                  // 0x104
            public uint thread_static_fields_size;
            public int thread_static_fields_offset;
            public uint flags;                                               // 0x110
            public uint token;                                               // 0x114
            public ushort method_count;                                      // 0x118
            public ushort property_count;
            public ushort field_count;
            public ushort event_count;
            public ushort nested_type_count;
            public ushort vtable_count;
            public ushort interfaces_count;                                  // 0x124
            public ushort interface_offsets_count;                           // 0x126
            public byte typeHierarchyDepth;                                  // 0x128
            public byte genericRecursionDepth;
            public byte rank;                                                // 0x12a
            public byte minimumAlignment;
            public byte packingSize;
            public Bitfield0 _bitfield0;                                     // 0x12d
            public Bitfield1 _bitfield1;                                     // 0x12e
            internal enum Bitfield0 : byte
            {
                BIT_initialized_and_no_error = 0,
                initialized_and_no_error = (1 << BIT_initialized_and_no_error),
                BIT_initialized = 1,
                initialized = (1 << BIT_initialized),
                BIT_enumtype = 2,
                enumtype = (1 << BIT_enumtype),
                BIT_nullabletype = 3,
                nullabletype = (1 << BIT_nullabletype),
                BIT_is_generic = 4,
                is_generic = (1 << BIT_is_generic),
                BIT_has_references = 5,
                has_references = (1 << BIT_has_references),
                BIT_init_pending = 6,
                init_pending = (1 << BIT_init_pending),
                BIT_size_init_pending = 7,
                size_init_pending = (1 << BIT_size_init_pending),
            }

            internal enum Bitfield1 : byte
            {
                BIT_size_inited = 0,
                size_inited = (1 << BIT_size_inited),
                BIT_has_finalize = 1,
                has_finalize = (1 << BIT_has_finalize),
                BIT_has_cctor = 2,
                has_cctor = (1 << BIT_has_cctor),
                BIT_is_blittable = 3,
                is_blittable = (1 << BIT_is_blittable),
                BIT_is_import_or_windows_runtime = 4,
                is_import_or_windows_runtime = (1 << BIT_is_import_or_windows_runtime),
                BIT_is_vtable_initialized = 5,
                is_vtable_initialized = (1 << BIT_is_vtable_initialized),
                BIT_is_byref_like = 6,
                is_byref_like = (1 << BIT_is_byref_like),
            }

        }

        internal class NativeStructWrapper : INativeClassStruct
        {
            public NativeStructWrapper(IntPtr ptr) => Pointer = ptr;
            private static int _bitfield0offset = Marshal.OffsetOf<Il2CppClass_108_0>(nameof(Il2CppClass_108_0._bitfield0)).ToInt32();
            private static int _bitfield1offset = Marshal.OffsetOf<Il2CppClass_108_0>(nameof(Il2CppClass_108_0._bitfield1)).ToInt32();
            public IntPtr Pointer { get; }
            private Il2CppClass_108_0* _ => (Il2CppClass_108_0*)Pointer;
            public IntPtr VTable => IntPtr.Add(Pointer, sizeof(Il2CppClass_108_0));
            public Il2CppClass* ClassPointer => (Il2CppClass*)Pointer;
            public INativeTypeStruct ByValArg => UnityVersionHandler.Wrap((Il2CppTypeStruct*)&_->byval_arg);
            public INativeTypeStruct ThisArg => UnityVersionHandler.Wrap((Il2CppTypeStruct*)&_->this_arg);
            public ref uint InstanceSize => ref _->instance_size;
            public ref ushort VtableCount => ref _->vtable_count;
            public ref ushort InterfaceCount => ref _->interfaces_count;
            public ref ushort InterfaceOffsetsCount => ref _->interface_offsets_count;
            public ref byte TypeHierarchyDepth => ref _->typeHierarchyDepth;
            public ref int NativeSize => ref _->native_size;
            public ref uint ActualSize => ref _->actualSize;
            public ref ushort MethodCount => ref _->method_count;
            public ref ushort FieldCount => ref _->field_count;
            public ref Il2CppClassAttributes Flags => ref *(Il2CppClassAttributes*)&_->flags;
            public ref IntPtr Name => ref *(IntPtr*)&_->name;
            public ref IntPtr Namespace => ref *(IntPtr*)&_->namespaze;
            public ref Il2CppImage* Image => ref _->image;
            public ref Il2CppClass* Parent => ref _->parent;
            public ref Il2CppClass* ElementClass => ref _->element_class;
            public ref Il2CppClass* CastClass => ref _->castClass;
            public ref Il2CppClass* DeclaringType => ref _->declaringType;
            public ref Il2CppClass* Class => ref _->klass;
            public ref Il2CppFieldInfo* Fields => ref _->fields;
            public ref Il2CppMethodInfo** Methods => ref _->methods;
            // No separate implementedInterfaces array exists in this layout; both views share the
            // merged interface table. Injecting Il2Cpp types that implement interfaces is unsupported.
            public ref Il2CppClass** ImplementedInterfaces => ref *(Il2CppClass***)&_->interfaceOffsets;
            public ref Il2CppRuntimeInterfaceOffsetPair* InterfaceOffsets => ref _->interfaceOffsets;
            public ref Il2CppClass** TypeHierarchy => ref _->typeHierarchy;
            public bool ValueType
            {
                get => ByValArg.ValueType && ThisArg.ValueType;
                set { }
            }
            public bool Initialized
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_initialized);
                set => this.SetBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_initialized, value);
            }
            public bool EnumType
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_enumtype);
                set => this.SetBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_enumtype, value);
            }
            public bool IsGeneric
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_is_generic);
                set => this.SetBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_is_generic, value);
            }
            public bool HasReferences
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_has_references);
                set => this.SetBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_has_references, value);
            }
            public bool SizeInited
            {
                get => this.CheckBit(_bitfield1offset, (int)Il2CppClass_108_0.Bitfield1.BIT_size_inited);
                set => this.SetBit(_bitfield1offset, (int)Il2CppClass_108_0.Bitfield1.BIT_size_inited, value);
            }
            public bool HasFinalize
            {
                get => this.CheckBit(_bitfield1offset, (int)Il2CppClass_108_0.Bitfield1.BIT_has_finalize);
                set => this.SetBit(_bitfield1offset, (int)Il2CppClass_108_0.Bitfield1.BIT_has_finalize, value);
            }
            public bool IsVtableInitialized
            {
                get => this.CheckBit(_bitfield1offset, (int)Il2CppClass_108_0.Bitfield1.BIT_is_vtable_initialized);
                set => this.SetBit(_bitfield1offset, (int)Il2CppClass_108_0.Bitfield1.BIT_is_vtable_initialized, value);
            }
            public bool InitializedAndNoError
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_initialized_and_no_error);
                set => this.SetBit(_bitfield0offset, (int)Il2CppClass_108_0.Bitfield0.BIT_initialized_and_no_error, value);
            }
        }

    }

}
