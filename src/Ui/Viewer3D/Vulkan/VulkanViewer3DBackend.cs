// brief-em3d-28 R-em3d28-1b — route A on Linux: native Vulkan through Vortice.Vulkan's MANAGED bindings
// (MIT; the loader, libvulkan.so.1, is the system's or the driver's). No native package (R-em3d28-1c).
//
// Lifted from tools/Viewer3dSpike's Vulkan half (brief 28 step 0, findings §8), whose harness passed on
// Linux arm64 under Mesa's software driver (0 B per orbit frame, 0 B managed allocation per frame, the
// pick reads back the right object, and its frame matches Metal's pixel for pixel to a mean 0.095).
// What the presentation path must be was read from Avalonia 12.0.3's own importers, not assumed:
//   * The image is exported as an OPAQUE POSIX FD. Avalonia's Vulkan compositor RE-CREATES the image
//     from it with fixed parameters — R8G8B8A8, optimal tiling, usage TRANSFER_SRC | TRANSFER_DST |
//     SAMPLED | COLOR_ATTACHMENT, MUTABLE_FORMAT, one mip, a DEDICATED allocation whose size must equal
//     MemorySize — and an opaque handle only imports into an identically-created image, so ours is made
//     exactly so.
//   * Both importers assume the image is in TRANSFER_SRC_OPTIMAL when their wait completes (the Vulkan
//     one transitions to it once and reads from it; the GLX one waits its semaphore with
//     GL_LAYOUT_TRANSFER_SRC_EXT). So every frame's render pass ends in TRANSFER_SRC_OPTIMAL.
//   * Synchronisation is BINARY semaphores, a pair per image: "ready" (we signal it when the frame is
//     done) and "released" (the compositor signals it when it has read the image). A binary semaphore
//     may be waited only after its signal was SUBMITTED, so before the GPU waits "released" the render
//     thread sees the compositor's update task complete; one that has not is a fault, never a loop.
//   * Each import takes ownership of its fd, so a fresh fd is exported for every import.
//   * The GLX compositor (Avalonia's Linux default) offers these handles only when the GL driver has
//     BOTH GL_EXT_memory_object_fd and GL_EXT_semaphore_fd; Mesa's software GL has only the first, and
//     Avalonia refuses it for GLX anyway — a software stack cannot host this pane.
//   * The device is matched to the compositor's by UUID (ICompositionGpuInterop.DeviceUuid).
//
// The shader is the generated scene.spv (Shaders/): one module, four entry points. tools/ShaderGen
// writes it with naga's ADJUST_COORDINATE_SPACE, which negates clip-space y in the vertex shader — so
// the image comes out upright with the SAME view-projection the Metal and D3D11 backends use. FlipY is
// therefore FALSE here, and a triangle keeps the on-screen winding it has on the other two APIs, so
// the front face is COUNTER-CLOCKWISE as it is there (the shader's front_facing draws the clip caps).
//
// Per frame the 400-byte uniform block (brief 29 added the field block) is copied into a persistently-mapped, host-coherent ring and
// bound as a dynamic uniform buffer — counted as uniform bytes, never geometry. Three frames in flight,
// each with its own command buffer, fence and 1×1 pick readback buffer, read when its fence has
// signalled, never waited for.

using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CircuitRF.Ui.Viewer3D.Vulkan;

internal sealed unsafe class VulkanViewer3DBackend : Viewer3DBackend
{
    private const int Ring = 3;

    /// <summary>How long a frame's fence may take before the GPU is declared stuck (a fault, never an
    /// endless wait: the render thread holds the render lock, and the UI thread takes it on detach).</summary>
    private const ulong FenceTimeoutNs = 2_000_000_000;
    /// <summary>brief-em3d-46 — one per-draw transform slot in the transform buffer: 80 bytes (brief-em3d-48: the matrix and the id offset), at an offset every
    /// minUniformBufferOffsetAlignment divides (the spec caps that at 256).</summary>
    private const int TransformStride = 256;
    /// <summary>Transform slots per frame slot: a preview's copies at first; brief-em3d-48's array elements (a slot each,
    /// and one for each one's box) grow it — the ring is re-made, once, between frames (EnsureTransformRing).</summary>
    private int _transformSlots = 1 + Scene3DFramePlan.MaxPreviewCopies;
    // ≥ the uniform block (2,016 bytes since brief-em3d-96's four field blocks) and a multiple of every minUniformBufferOffsetAlignment (≤ 256)
    private const int UniformStride = (Scene3DFramePlan.UniformBytes + 255) / 256 * 256;
    private const VkFormat ColorFormat = VkFormat.R8G8B8A8Unorm;
    private const VkFormat DepthFormat = VkFormat.D32Sfloat;
    private const VkImageUsageFlags TargetUsage =
        VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled | VkImageUsageFlags.ColorAttachment;
    private const string FdHandle = KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor;
    private const string FdSemaphore = KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor;

    private VkInstance _instance;
    private VkInstanceApi? _vi;
    private VkPhysicalDevice _physical;
    private VkDevice _device;
    private VkDeviceApi? _api;
    private VkQueue _queue;
    private uint _queueFamily;
    private byte[] _deviceUuid = [];
    private bool _canExport;
    /// <summary>Whether the device enabled <c>depthBiasClamp</c>: what lies on a face has its bias clamped (Scene3DFramePlan.DepthBias), and
    /// without the feature a clamp must be 0 — such a draw then takes no slope factor, the constant alone as before.</summary>
    private bool _biasClamp;
    private VkPhysicalDeviceMemoryProperties _mem;
    private string _description = "Vulkan (no device yet)";

    private VkRenderPass _rpColor, _rpPick;
    private VkDescriptorSetLayout _setLayout;
    private VkPipelineLayout _layout;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private VkShaderModule _module;
    private VkPipeline _pOpaque, _pTrans, _pLines, _pPick, _pField, _pEdges, _pTop, _pGrid;
    /// <summary>brief-em3d-101 — the image pipelines, the image vertex stream, the one sampler, and set 1's layout: a sampled image
    /// (binding 0) and a sampler (binding 1). Each texture owns its own one-set descriptor pool (<see cref="Texture"/>), so a
    /// re-elaboration can never exhaust a shared pool: the set lives and dies with its texture (src/Ui/Viewer3D/RESOLVED.md).</summary>
    private VkPipeline _pImage, _pImageTrans;
    private VkDescriptorSetLayout _imageSetLayout;
    private VkSampler _sampler;
    private (VkBuffer Buf, VkDeviceMemory Mem) _imageVb;
    private readonly Scene3DTextureResidency<Texture?> _textures = new();

    /// <summary>brief-em3d-101 — one uploaded image: its image, memory and view, and the descriptor set (in a pool of its own) that
    /// binds it at set 1.</summary>
    private sealed class Texture
    {
        public VkImage Image;
        public VkDeviceMemory Memory;
        public VkImageView View;
        public VkDescriptorPool Pool;
        public VkDescriptorSet Set;
    }
    private VkCommandPool _cmdPool;
    private (VkBuffer Buf, VkDeviceMemory Mem) _ub;
    /// <summary>brief-em3d-46 — the per-draw transforms (binding 1), <see cref="_transformSlots"/> per frame slot.</summary>
    private (VkBuffer Buf, VkDeviceMemory Mem) _tb;
    private byte* _tMapped;
    private byte* _uMapped;
    private readonly VkCommandBuffer[] _cmd = new VkCommandBuffer[Ring];
    private readonly VkFence[] _fence = new VkFence[Ring];
    private readonly bool[] _submitted = new bool[Ring];
    private readonly (VkBuffer Buf, VkDeviceMemory Mem)[] _pickBuf = new (VkBuffer, VkDeviceMemory)[Ring];
    private readonly byte*[] _pickMapped = new byte*[Ring];
    private readonly bool[] _pickPending = new bool[Ring];
    private readonly long[] _pickFrame = new long[Ring];
    private Target _pickId = null!, _pickPos = null!, _pickDepth = null!;
    private VkFramebuffer _pickFb;
    private long _frame;
    private (VkBuffer Buf, VkDeviceMemory Mem) _vb, _ib, _lines, _field;
    private int _fieldCount;
    private readonly (VkBuffer Buf, VkDeviceMemory Mem)[] _overlays = new (VkBuffer, VkDeviceMemory)[3];

    /// <summary>An image with its memory and view.</summary>
    private sealed class Target
    {
        public VkImage Image;
        public VkDeviceMemory Memory;
        public ulong MemorySize;
        public VkImageView View;
    }

    private sealed class Image
    {
        public Target Color = null!, Depth = null!;
        public VkFramebuffer Framebuffer;
        public int Width, Height;
        public VkSemaphore Ready, Released;
        public ICompositionImportedGpuImage? Imported;
        public ICompositionImportedGpuSemaphore? ReadyImported, ReleasedImported;
        public Task? Pending;
        /// <summary>The compositor was handed this image; its "released" signal is owed to our next frame.</summary>
        public bool ReleaseOwed;
        /// <summary>"ready" was signalled by a frame that has not been presented yet.</summary>
        public bool ReadySignalled;
    }
    private Image[] _images = [];

    public override string Description => _description;

    private VkDeviceApi Api => _api ?? CreateDevice(null);

    // ── device ──────────────────────────────────────────────────────────────────────────────

    private VkDeviceApi CreateDevice(byte[]? uuid)
    {
        if (vkInitialize() != VkResult.Success)
            throw new Viewer3DPresentFault("This machine has no Vulkan loader (libvulkan.so.1).");
        // An earlier attempt that failed after making its instance left it here: never leak one per try.
        _vi?.vkDestroyInstance(null);
        _vi = null;
        var app = new VkApplicationInfo { apiVersion = VkVersion.Version_1_1 };
        fixed (byte* name = "circuitRF 3D view"u8) app.pApplicationName = name;
        var ici = new VkInstanceCreateInfo { pApplicationInfo = &app };
        VkInstance inst;
        Check(vkCreateInstance(&ici, &inst), "vkCreateInstance");
        _instance = inst;
        var vi = GetApi(inst);
        _vi = vi;

        uint n = 0;
        Check(vi.vkEnumeratePhysicalDevices(&n, null), "vkEnumeratePhysicalDevices");
        var devs = new VkPhysicalDevice[n];
        fixed (VkPhysicalDevice* pd = devs) Check(vi.vkEnumeratePhysicalDevices(&n, pd), "vkEnumeratePhysicalDevices");
        var seen = new List<string>();
        foreach (var dev in devs)
        {
            var id = new VkPhysicalDeviceIDProperties();
            var p2 = new VkPhysicalDeviceProperties2 { pNext = &id };
            vi.vkGetPhysicalDeviceProperties2(dev, &p2);
            var u = new ReadOnlySpan<byte>(id.deviceUUID, 16).ToArray();
            seen.Add(Convert.ToHexString(u));
            if (uuid is { Length: 16 } && !u.AsSpan().SequenceEqual(uuid)) continue;
            uint qn = 0;
            vi.vkGetPhysicalDeviceQueueFamilyProperties(dev, &qn, null);
            var qf = new VkQueueFamilyProperties[qn];
            fixed (VkQueueFamilyProperties* pq = qf) vi.vkGetPhysicalDeviceQueueFamilyProperties(dev, &qn, pq);
            int family = Array.FindIndex(qf, q => (q.queueFlags & VkQueueFlags.Graphics) != 0);
            if (family < 0) continue;
            _physical = dev;
            _queueFamily = (uint)family;
            _deviceUuid = u;
            _description = "Vulkan — " + Marshal.PtrToStringUTF8((nint)p2.properties.deviceName);
            break;
        }
        if (_physical.Handle == 0)
            throw new Viewer3DPresentFault(uuid is { Length: 16 }
                ? $"No Vulkan device has the compositor's UUID {Convert.ToHexString(uuid)} (devices: {string.Join(", ", seen)})."
                : "No Vulkan device has a graphics queue.");

        uint en = 0;
        vi.vkEnumerateDeviceExtensionProperties(_physical, null, &en, null);
        var ext = new VkExtensionProperties[en];
        fixed (VkExtensionProperties* pe = ext) vi.vkEnumerateDeviceExtensionProperties(_physical, null, &en, pe);
        bool Has(string e) { foreach (var x in ext) if (Marshal.PtrToStringUTF8((nint)x.extensionName) == e) return true; return false; }
        _canExport = Has("VK_KHR_external_memory_fd") && Has("VK_KHR_external_semaphore_fd");

        VkPhysicalDeviceFeatures supported;
        vi.vkGetPhysicalDeviceFeatures(_physical, &supported);
        _biasClamp = supported.depthBiasClamp;
        var features = new VkPhysicalDeviceFeatures { depthBiasClamp = _biasClamp };

        float prio = 1f;
        var qci = new VkDeviceQueueCreateInfo { queueFamilyIndex = _queueFamily, queueCount = 1, pQueuePriorities = &prio };
        fixed (byte* e1 = "VK_KHR_external_memory_fd"u8)
        fixed (byte* e2 = "VK_KHR_external_semaphore_fd"u8)
        {
            byte** names = stackalloc byte*[2] { e1, e2 };
            var dci = new VkDeviceCreateInfo
            {
                queueCreateInfoCount = 1, pQueueCreateInfos = &qci,
                enabledExtensionCount = _canExport ? 2u : 0u, ppEnabledExtensionNames = names,
                pEnabledFeatures = &features,
            };
            VkDevice device;
            Check(vi.vkCreateDevice(_physical, &dci, null, &device), "vkCreateDevice");
            _device = device;
        }
        var api = GetApi(inst, _device);
        _api = api;
        VkQueue queue;
        api.vkGetDeviceQueue(_queueFamily, 0, &queue);
        if (queue.Handle == 0) throw new Viewer3DPresentFault("Vulkan returned no queue.");
        _queue = queue;
        VkPhysicalDeviceMemoryProperties mp;
        vi.vkGetPhysicalDeviceMemoryProperties(_physical, &mp);
        _mem = mp;
        BuildPipelines(api);
        return api;
    }

    private static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success) throw new Viewer3DPresentFault($"{what} returned {r}.");
    }

    private uint MemoryType(uint bits, VkMemoryPropertyFlags want)
    {
        for (uint i = 0; i < _mem.memoryTypeCount; i++)
            if ((bits & (1u << (int)i)) != 0 && (_mem.memoryTypes[(int)i].propertyFlags & want) == want) return i;
        throw new Viewer3DPresentFault($"Vulkan has no {want} memory type for the 3D view.");
    }

    private void BuildPipelines(VkDeviceApi api)
    {
        var cpi = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = _queueFamily };
        VkCommandPool cmdPool;
        Check(api.vkCreateCommandPool(&cpi, null, &cmdPool), "vkCreateCommandPool");
        _cmdPool = cmdPool;

        var spirv = Viewer3DShaders.Spirv;
        fixed (byte* code = spirv)
        {
            var smi = new VkShaderModuleCreateInfo { codeSize = (nuint)spirv.Length, pCode = (uint*)code };
            VkShaderModule sm;
            Check(api.vkCreateShaderModule(&smi, null, &sm), "vkCreateShaderModule");
            _module = sm;
        }

        _rpColor = RenderPass(api, pick: false);
        _rpPick = RenderPass(api, pick: true);

        var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
        bindings[0] = new VkDescriptorSetLayoutBinding
        {
            binding = 0, descriptorType = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
        };
        // brief-em3d-46 — the per-draw transform: a second dynamic uniform, re-offset per draw that changes it.
        bindings[1] = new VkDescriptorSetLayoutBinding
        {
            binding = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex,
        };
        var dsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 2, pBindings = bindings };
        VkDescriptorSetLayout setLayout;
        Check(api.vkCreateDescriptorSetLayout(&dsl, null, &setLayout), "vkCreateDescriptorSetLayout");
        _setLayout = setLayout;
        // brief-em3d-101 — set 1: an image draw's texture and its sampler (tools/ShaderGen's binding map).
        var ib = stackalloc VkDescriptorSetLayoutBinding[2];
        ib[0] = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VkDescriptorType.SampledImage, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment };
        ib[1] = new VkDescriptorSetLayoutBinding { binding = 1, descriptorType = VkDescriptorType.Sampler, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment };
        var idsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 2, pBindings = ib };
        VkDescriptorSetLayout imageLayout;
        Check(api.vkCreateDescriptorSetLayout(&idsl, null, &imageLayout), "vkCreateDescriptorSetLayout");
        _imageSetLayout = imageLayout;
        var both = stackalloc VkDescriptorSetLayout[2] { setLayout, imageLayout };
        var pli = new VkPipelineLayoutCreateInfo { setLayoutCount = 2, pSetLayouts = both };
        VkPipelineLayout layout;
        Check(api.vkCreatePipelineLayout(&pli, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;

        _pOpaque = Pipeline(api, _rpColor, "fs_color"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 1);
        _pTrans = Pipeline(api, _rpColor, "fs_color"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1);
        _pLines = Pipeline(api, _rpColor, "fs_line"u8, VkPrimitiveTopology.LineList, blend: false, depthWrite: true, targets: 1);
        _pPick = Pipeline(api, _rpPick, "fs_pick"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 2);
        _pField = Pipeline(api, _rpColor, "fs_field"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 1, field: true);
        // brief-em3d-43 — the selection's edges and its face on top: no depth test.
        _pEdges = Pipeline(api, _rpColor, "fs_edge"u8, VkPrimitiveTopology.LineList, blend: true, depthWrite: false, targets: 1, depthTest: false);
        _pTop = Pipeline(api, _rpColor, "fs_top"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, depthTest: false);
        // brief-em3d-45 — the drawing grid: no vertex input at all, depth test without write, blend.
        _pGrid = Pipeline(api, _rpColor, "fs_grid"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, grid: true);
        // brief-em3d-101 — an image: blended always (an opaque image's alpha is 1); the opaque one writes depth.
        _pImage = Pipeline(api, _rpColor, "fs_image"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: true, targets: 1, image: true);
        _pImageTrans = Pipeline(api, _rpColor, "fs_image"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, image: true);
        var sci = new VkSamplerCreateInfo
        {
            magFilter = VkFilter.Linear, minFilter = VkFilter.Linear, mipmapMode = VkSamplerMipmapMode.Linear,
            addressModeU = VkSamplerAddressMode.ClampToEdge, addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge, minLod = 0, maxLod = 1000f,
        };
        VkSampler sampler;
        Check(api.vkCreateSampler(&sci, null, &sampler), "vkCreateSampler");
        _sampler = sampler;

        // two uniform blocks (pick, colour) per frame slot — host-coherent, mapped once
        _ub = NewBuffer(api, Ring * 2 * UniformStride, VkBufferUsageFlags.UniformBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* um;
        Check(api.vkMapMemory(_ub.Mem, 0, VK_WHOLE_SIZE, 0, &um), "vkMapMemory");
        _uMapped = (byte*)um;
        _tb = NewBuffer(api, Ring * _transformSlots * TransformStride, VkBufferUsageFlags.UniformBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* tm;
        Check(api.vkMapMemory(_tb.Mem, 0, VK_WHOLE_SIZE, 0, &tm), "vkMapMemory");
        _tMapped = (byte*)tm;
        var ps = new VkDescriptorPoolSize { type = VkDescriptorType.UniformBufferDynamic, descriptorCount = 2 };
        var dpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 1, pPoolSizes = &ps };
        VkDescriptorPool dp;
        Check(api.vkCreateDescriptorPool(&dpi, null, &dp), "vkCreateDescriptorPool");
        _pool = dp;
        var dai = new VkDescriptorSetAllocateInfo { descriptorPool = dp, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(api.vkAllocateDescriptorSets(&dai, &set), "vkAllocateDescriptorSets");
        _set = set;
        var dbi = new VkDescriptorBufferInfo { buffer = _ub.Buf, offset = 0, range = Scene3DFramePlan.UniformBytes };
        var tbi = new VkDescriptorBufferInfo { buffer = _tb.Buf, offset = 0, range = Scene3DFramePlan.TransformBytesPerDraw };
        var writes = stackalloc VkWriteDescriptorSet[2];
        writes[0] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &dbi };
        writes[1] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &tbi };
        api.vkUpdateDescriptorSets(2, writes, 0, null);

        var cai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = Ring };
        fixed (VkCommandBuffer* c = _cmd) Check(api.vkAllocateCommandBuffers(&cai, c), "vkAllocateCommandBuffers");
        for (int i = 0; i < Ring; i++)
        {
            var fci = new VkFenceCreateInfo();
            VkFence f;
            Check(api.vkCreateFence(&fci, null, &f), "vkCreateFence");
            _fence[i] = f;
            _pickBuf[i] = NewBuffer(api, 32, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            void* pm;
            Check(api.vkMapMemory(_pickBuf[i].Mem, 0, VK_WHOLE_SIZE, 0, &pm), "vkMapMemory");
            _pickMapped[i] = (byte*)pm;
        }
        _pickId = NewImage(api, 1, 1, VkFormat.R32G32Uint, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc, VkImageAspectFlags.Color, export: false);
        _pickPos = NewImage(api, 1, 1, VkFormat.R32G32B32A32Sfloat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc, VkImageAspectFlags.Color, export: false);
        _pickDepth = NewImage(api, 1, 1, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, export: false);
        _pickFb = Framebuffer(api, _rpPick, [_pickId.View, _pickPos.View, _pickDepth.View], 1, 1);
    }

    private VkRenderPass RenderPass(VkDeviceApi api, bool pick)
    {
        int colors = pick ? 2 : 1;
        var att = stackalloc VkAttachmentDescription[3];
        for (int i = 0; i < colors; i++)
            att[i] = new VkAttachmentDescription
            {
                format = pick ? (i == 0 ? VkFormat.R32G32Uint : VkFormat.R32G32B32A32Sfloat) : ColorFormat,
                samples = VkSampleCountFlags.Count1, loadOp = VkAttachmentLoadOp.Clear, storeOp = VkAttachmentStoreOp.Store,
                stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
                // TRANSFER_SRC_OPTIMAL: what both of Avalonia's importers read from, and what the pick copy reads
                initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.TransferSrcOptimal,
            };
        att[colors] = new VkAttachmentDescription
        {
            format = DepthFormat, samples = VkSampleCountFlags.Count1, loadOp = VkAttachmentLoadOp.Clear, storeOp = VkAttachmentStoreOp.DontCare,
            stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
        };
        var cref = stackalloc VkAttachmentReference[2];
        cref[0] = new VkAttachmentReference { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        cref[1] = new VkAttachmentReference { attachment = 1, layout = VkImageLayout.ColorAttachmentOptimal };
        var dref = new VkAttachmentReference { attachment = (uint)colors, layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var sub = new VkSubpassDescription
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics, colorAttachmentCount = (uint)colors, pColorAttachments = cref,
            pDepthStencilAttachment = &dref,
        };
        var deps = stackalloc VkSubpassDependency[2];
        // in: the previous reader (the compositor's copy, or our own pick copy) is done before we clear
        deps[0] = new VkSubpassDependency
        {
            srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput | VkPipelineStageFlags.Transfer | VkPipelineStageFlags.LateFragmentTests,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput | VkPipelineStageFlags.EarlyFragmentTests,
            // The previous frame's attachment WRITES, made available before this one's (write after write).
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.DepthStencilAttachmentWrite,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.DepthStencilAttachmentWrite,
        };
        // out: the colour writes are visible to a transfer read (the compositor's, or the pick copy)
        deps[1] = new VkSubpassDependency
        {
            srcSubpass = 0, dstSubpass = VK_SUBPASS_EXTERNAL,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput, dstStageMask = VkPipelineStageFlags.Transfer | VkPipelineStageFlags.BottomOfPipe,
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite, dstAccessMask = VkAccessFlags.TransferRead,
        };
        var rpi = new VkRenderPassCreateInfo
        {
            attachmentCount = (uint)(colors + 1), pAttachments = att, subpassCount = 1, pSubpasses = &sub,
            dependencyCount = 2, pDependencies = deps,
        };
        VkRenderPass rp;
        Check(api.vkCreateRenderPass(&rpi, null, &rp), "vkCreateRenderPass");
        return rp;
    }

    /// <summary>3D editor round 3 / bugs round 9 — the polygon offset of a draw's depth tie (Scene3DFramePlan.DepthBias), set only
    /// when it changes between draws. A clamp other than 0 needs the device's <c>depthBiasClamp</c> (<see cref="_biasClamp"/>).</summary>
    private void SetTie(VkDeviceApi api, VkCommandBuffer cb, Scene3DDepthTie tie)
    {
        var (constant, slope, clamp) = Scene3DFramePlan.DepthBias(tie);
        if (clamp != 0 && !_biasClamp) (slope, clamp) = (0, 0);
        api.vkCmdSetDepthBias(cb, constant, clamp, slope);
    }

    private VkPipeline Pipeline(VkDeviceApi api, VkRenderPass rp, ReadOnlySpan<byte> fragmentEntry, VkPrimitiveTopology topology,
                                bool blend, bool depthWrite, int targets, bool field = false, bool depthTest = true, bool grid = false,
                                bool image = false)
    {
        fixed (byte* vsName = field ? "vs_field"u8 : grid ? "vs_grid"u8 : image ? "vs_image"u8 : "vs"u8)
        fixed (byte* fsName = fragmentEntry)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = _module, pName = vsName };
            stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = _module, pName = fsName };
            var vbd = new VkVertexInputBindingDescription
            {
                binding = 0, stride = field ? (uint)FieldVertex.Stride : image ? (uint)Scene3DImageVertex.Stride : Scene3DVertex.Stride,
                inputRate = VkVertexInputRate.Vertex,
            };
            var attrs = stackalloc VkVertexInputAttributeDescription[5];
            uint attrCount = field ? 3u : image ? 5u : 4u;
            if (image)
            {
                // brief-em3d-101 — Scene3DImageVertex: position, texture coordinate, id, face, colour.
                attrs[0] = new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };
                attrs[1] = new VkVertexInputAttributeDescription { location = 1, binding = 0, format = VkFormat.R32G32Sfloat, offset = 12 };
                attrs[2] = new VkVertexInputAttributeDescription { location = 2, binding = 0, format = VkFormat.R32Uint, offset = 20 };
                attrs[3] = new VkVertexInputAttributeDescription { location = 3, binding = 0, format = VkFormat.R32Uint, offset = 24 };
                attrs[4] = new VkVertexInputAttributeDescription { location = 4, binding = 0, format = VkFormat.R8G8B8A8Unorm, offset = 28 };
            }
            else if (field)
            {
                // brief-em3d-29 — FieldVertex: position, the value's real part, its imaginary part.
                for (uint k = 0; k < 3; k++)
                    attrs[k] = new VkVertexInputAttributeDescription { location = k, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 12 * k };
            }
            else
            {
                attrs[0] = new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };
                attrs[1] = new VkVertexInputAttributeDescription { location = 1, binding = 0, format = VkFormat.R32Uint, offset = 12 };
                attrs[2] = new VkVertexInputAttributeDescription { location = 2, binding = 0, format = VkFormat.R8G8B8A8Unorm, offset = 16 };
                attrs[3] = new VkVertexInputAttributeDescription { location = 3, binding = 0, format = VkFormat.R32Uint, offset = 20 };
            }
            var vin = grid
                ? new VkPipelineVertexInputStateCreateInfo()
                : new VkPipelineVertexInputStateCreateInfo
                {
                    vertexBindingDescriptionCount = 1, pVertexBindingDescriptions = &vbd,
                    vertexAttributeDescriptionCount = attrCount, pVertexAttributeDescriptions = attrs,
                };
            var ia = new VkPipelineInputAssemblyStateCreateInfo { topology = topology };
            var vp = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            // cull none; counter-clockwise front faces — see the header on why FlipY is false
            var rs = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill, cullMode = VkCullModeFlags.None, frontFace = VkFrontFace.CounterClockwise, lineWidth = 1f,
                // 3D editor round 3 / bugs round 9 — the depth tie's polygon offset is DYNAMIC state (SetTie), on every pipeline so
                // no bind disturbs it: four ties across three triangle pipelines would otherwise be twelve static twins.
                depthBiasEnable = true,
            };
            var ms = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var ds = new VkPipelineDepthStencilStateCreateInfo { depthTestEnable = depthTest, depthWriteEnable = depthWrite, depthCompareOp = VkCompareOp.LessOrEqual };
            var cba = stackalloc VkPipelineColorBlendAttachmentState[2];
            for (int i = 0; i < targets; i++)
                cba[i] = new VkPipelineColorBlendAttachmentState
                {
                    blendEnable = blend,
                    srcColorBlendFactor = VkBlendFactor.SrcAlpha, dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha, colorBlendOp = VkBlendOp.Add,
                    // the shared image's alpha stays 1 (R-em3d28-1d)
                    srcAlphaBlendFactor = VkBlendFactor.One, dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha, alphaBlendOp = VkBlendOp.Add,
                    colorWriteMask = VkColorComponentFlags.All,
                };
            var cb = new VkPipelineColorBlendStateCreateInfo { attachmentCount = (uint)targets, pAttachments = cba };
            var dyn = stackalloc VkDynamicState[3] { VkDynamicState.Viewport, VkDynamicState.Scissor, VkDynamicState.DepthBias };
            var dy = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 3, pDynamicStates = dyn };
            var gpi = new VkGraphicsPipelineCreateInfo
            {
                stageCount = 2, pStages = stages, pVertexInputState = &vin, pInputAssemblyState = &ia, pViewportState = &vp,
                pRasterizationState = &rs, pMultisampleState = &ms, pDepthStencilState = &ds, pColorBlendState = &cb, pDynamicState = &dy,
                layout = _layout, renderPass = rp, subpass = 0,
            };
            VkPipeline p;
            Check(api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &gpi, null, &p), "vkCreateGraphicsPipelines");
            return p;
        }
    }

    private (VkBuffer, VkDeviceMemory) NewBuffer(VkDeviceApi api, long size, VkBufferUsageFlags usage, VkMemoryPropertyFlags props)
    {
        var bci = new VkBufferCreateInfo { size = (ulong)size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        VkBuffer b;
        Check(api.vkCreateBuffer(&bci, null, &b), "vkCreateBuffer");
        VkMemoryRequirements req;
        api.vkGetBufferMemoryRequirements(b, &req);
        var mai = new VkMemoryAllocateInfo { allocationSize = req.size, memoryTypeIndex = MemoryType(req.memoryTypeBits, props) };
        VkDeviceMemory m = default;
        try
        {
            // out of memory here (a large photo's staging buffer) must not leave the buffer behind
            Check(api.vkAllocateMemory(&mai, null, &m), "vkAllocateMemory");
            Check(api.vkBindBufferMemory(b, m, 0), "vkBindBufferMemory");
        }
        catch
        {
            if (m.Handle != 0) api.vkFreeMemory(m, null);
            api.vkDestroyBuffer(b, null);
            throw;
        }
        return (b, m);
    }

    private Target NewImage(VkDeviceApi api, int w, int h, VkFormat fmt, VkImageUsageFlags usage, VkImageAspectFlags aspect, bool export)
    {
        // exactly the parameters Avalonia's importer re-creates the image with (header)
        var emi = new VkExternalMemoryImageCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD };
        var ici = new VkImageCreateInfo
        {
            pNext = export ? &emi : null,
            flags = export ? VkImageCreateFlags.MutableFormat : VkImageCreateFlags.None,
            imageType = VkImageType.Image2D, format = fmt, extent = new VkExtent3D { width = (uint)w, height = (uint)h, depth = 1 },
            mipLevels = 1, arrayLayers = 1, samples = VkSampleCountFlags.Count1, tiling = VkImageTiling.Optimal,
            usage = usage, sharingMode = VkSharingMode.Exclusive, initialLayout = VkImageLayout.Undefined,
        };
        var t = new Target();
        VkImage img;
        Check(api.vkCreateImage(&ici, null, &img), "vkCreateImage");
        t.Image = img;
        VkMemoryRequirements req;
        api.vkGetImageMemoryRequirements(img, &req);
        var ded = new VkMemoryDedicatedAllocateInfo { image = img };
        var exp = new VkExportMemoryAllocateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD, pNext = &ded };
        var mai = new VkMemoryAllocateInfo
        {
            pNext = export ? &exp : null, allocationSize = req.size,
            memoryTypeIndex = MemoryType(req.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal),
        };
        VkDeviceMemory m;
        Check(api.vkAllocateMemory(&mai, null, &m), "vkAllocateMemory");
        Check(api.vkBindImageMemory(img, m, 0), "vkBindImageMemory");
        t.Memory = m;
        t.MemorySize = req.size;
        var vci = new VkImageViewCreateInfo
        {
            image = img, viewType = VkImageViewType.Image2D, format = fmt,
            subresourceRange = new VkImageSubresourceRange { aspectMask = aspect, levelCount = 1, layerCount = 1 },
        };
        VkImageView v;
        Check(api.vkCreateImageView(&vci, null, &v), "vkCreateImageView");
        t.View = v;
        return t;
    }

    private static void Destroy(VkDeviceApi api, Target? t)
    {
        if (t is null) return;
        api.vkDestroyImageView(t.View, null); api.vkDestroyImage(t.Image, null); api.vkFreeMemory(t.Memory, null);
    }

    private static VkFramebuffer Framebuffer(VkDeviceApi api, VkRenderPass rp, VkImageView[] views, int w, int h)
    {
        fixed (VkImageView* pv = views)
        {
            var fci = new VkFramebufferCreateInfo { renderPass = rp, attachmentCount = (uint)views.Length, pAttachments = pv, width = (uint)w, height = (uint)h, layers = 1 };
            VkFramebuffer fb;
            Check(api.vkCreateFramebuffer(&fci, null, &fb), "vkCreateFramebuffer");
            return fb;
        }
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    public override void UploadScene(Scene3DModel scene)
    {
        var api = Api;
        Free(api, ref _vb); Free(api, ref _ib); Free(api, ref _lines);
        fixed (Scene3DVertex* p = scene.Vertices) _vb = Upload(api, p, scene.Vertices.Length * Scene3DVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        fixed (uint* p = scene.Indices) _ib = Upload(api, p, scene.Indices.Length * 4, VkBufferUsageFlags.IndexBuffer);
        fixed (Scene3DVertex* p = scene.LineVertices) _lines = Upload(api, p, scene.LineVertices.Length * Scene3DVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        Free(api, ref _imageVb);
        fixed (Scene3DImageVertex* p = scene.ImageVertices) _imageVb = Upload(api, p, scene.ImageVertices.Length * Scene3DImageVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        _textures.Sync(scene, UploadTexture, ReleaseTexture);
    }

    /// <summary>
    /// brief-em3d-101 R-em3d101-4d — one image: an RGBA8 UNORM image (never _SRGB) with every CPU-built mip level, staged in ONE
    /// buffer and copied level by level between two layout transitions (undefined → transfer destination → shader read), then a
    /// view over all its levels and a descriptor set from a pool of its own. Once per <see cref="Scene3DTexture"/>.
    /// </summary>
    private Texture? UploadTexture(Scene3DTexture t)
    {
        var api = Api;
        var levels = t.Levels();
        long total = levels.Sum(l => (long)l.Rgba.Length);
        Counters.CountUpload(total);
        var staging = NewBuffer(api, total, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        // A failure part-way (out of device memory on a large photo, above all) releases what was made before it — the staging
        // buffer always, the image, its memory, view and pool when the upload does not complete — and is then thrown as before.
        var tex = new Texture();
        bool done = false;
        try
        {
            void* p;
            Check(api.vkMapMemory(staging.Item2, 0, (ulong)total, 0, &p), "vkMapMemory");
            long at = 0;
            foreach (var l in levels) { l.Rgba.AsSpan().CopyTo(new Span<byte>((byte*)p + at, l.Rgba.Length)); at += l.Rgba.Length; }
            api.vkUnmapMemory(staging.Item2);

            uint mips = (uint)levels.Count;
            var ici = new VkImageCreateInfo
            {
                imageType = VkImageType.Image2D, format = VkFormat.R8G8B8A8Unorm,
                extent = new VkExtent3D { width = (uint)levels[0].Width, height = (uint)levels[0].Height, depth = 1 },
                mipLevels = mips, arrayLayers = 1, samples = VkSampleCountFlags.Count1, tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, sharingMode = VkSharingMode.Exclusive,
                initialLayout = VkImageLayout.Undefined,
            };
            VkImage created;
            Check(api.vkCreateImage(&ici, null, &created), "vkCreateImage");
            var img = created;
            tex.Image = img;
            VkMemoryRequirements req;
            api.vkGetImageMemoryRequirements(img, &req);
            var mai = new VkMemoryAllocateInfo { allocationSize = req.size, memoryTypeIndex = MemoryType(req.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal) };
            VkDeviceMemory mem;
            Check(api.vkAllocateMemory(&mai, null, &mem), "vkAllocateMemory");
            tex.Memory = mem;
            Check(api.vkBindImageMemory(img, mem, 0), "vkBindImageMemory");
            var all = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.Color, levelCount = mips, layerCount = 1 };
            OneShot(api, cb =>
            {
                var toDst = new VkImageMemoryBarrier
                {
                    srcAccessMask = 0, dstAccessMask = VkAccessFlags.TransferWrite,
                    oldLayout = VkImageLayout.Undefined, newLayout = VkImageLayout.TransferDstOptimal,
                    srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, image = img, subresourceRange = all,
                };
                api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.Transfer, 0, 0, null, 0, null, 1, &toDst);
                var regions = new VkBufferImageCopy[levels.Count];
                ulong offset = 0;
                for (int k = 0; k < levels.Count; k++)
                {
                    regions[k] = new VkBufferImageCopy
                    {
                        bufferOffset = offset,
                        imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, mipLevel = (uint)k, layerCount = 1 },
                        imageExtent = new VkExtent3D { width = (uint)levels[k].Width, height = (uint)levels[k].Height, depth = 1 },
                    };
                    offset += (ulong)levels[k].Rgba.Length;
                }
                fixed (VkBufferImageCopy* r = regions)
                    api.vkCmdCopyBufferToImage(cb, staging.Item1, img, VkImageLayout.TransferDstOptimal, (uint)regions.Length, r);
                var toRead = new VkImageMemoryBarrier
                {
                    srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.ShaderRead,
                    oldLayout = VkImageLayout.TransferDstOptimal, newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, image = img, subresourceRange = all,
                };
                api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.FragmentShader, 0, 0, null, 0, null, 1, &toRead);
            });
            var vci = new VkImageViewCreateInfo { image = img, viewType = VkImageViewType.Image2D, format = VkFormat.R8G8B8A8Unorm, subresourceRange = all };
            VkImageView view;
            Check(api.vkCreateImageView(&vci, null, &view), "vkCreateImageView");
            tex.View = view;

            // A pool of exactly one set, owned by this texture and destroyed with it: no shared pool to size, none to exhaust.
            var sizes = stackalloc VkDescriptorPoolSize[2];
            sizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.SampledImage, descriptorCount = 1 };
            sizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.Sampler, descriptorCount = 1 };
            var dpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 2, pPoolSizes = sizes };
            VkDescriptorPool pool;
            Check(api.vkCreateDescriptorPool(&dpi, null, &pool), "vkCreateDescriptorPool");
            tex.Pool = pool;
            var layout = _imageSetLayout;
            var dai = new VkDescriptorSetAllocateInfo { descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &layout };
            VkDescriptorSet set;
            Check(api.vkAllocateDescriptorSets(&dai, &set), "vkAllocateDescriptorSets");
            tex.Set = set;
            var ii = new VkDescriptorImageInfo { imageView = view, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var si = new VkDescriptorImageInfo { sampler = _sampler };
            var writes = stackalloc VkWriteDescriptorSet[2];
            writes[0] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.SampledImage, pImageInfo = &ii };
            writes[1] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.Sampler, pImageInfo = &si };
            api.vkUpdateDescriptorSets(2, writes, 0, null);
            done = true;
            return tex;
        }
        finally
        {
            api.vkDestroyBuffer(staging.Item1, null);
            api.vkFreeMemory(staging.Item2, null);
            if (!done) ReleaseTexture(tex);
        }
    }

    /// <summary>A texture no scene draws any more. A frame in flight may still sample it, so the GPU is waited for first — once
    /// per scene change that drops an image, never per frame. Destroying its pool frees its set.</summary>
    private void ReleaseTexture(Texture? t)
    {
        if (t is null || _api is not { } api) return;
        api.vkDeviceWaitIdle();
        if (t.Pool.Handle != 0) api.vkDestroyDescriptorPool(t.Pool, null);
        if (t.View.Handle != 0) api.vkDestroyImageView(t.View, null);
        if (t.Image.Handle != 0) api.vkDestroyImage(t.Image, null);
        if (t.Memory.Handle != 0) api.vkFreeMemory(t.Memory, null);
    }

    /// <summary>brief-em3d-43 gate 6 — the changed ranges only, staged and copied into the buffers in place.
    /// A frame in flight may still read them, so the GPU is waited for first, as a whole upload does.</summary>
    public override void PatchScene(Scene3DModel scene, Scene3DPatch patch)
    {
        var api = Api;
        api.vkDeviceWaitIdle();
        foreach (var r in patch.Ranges)
        {
            var target = r.Buffer switch { Scene3DPatchBuffer.Vertices => _vb, Scene3DPatchBuffer.Indices => _ib, _ => _lines };
            if (target.Buf.Handle == 0) { UploadScene(scene); return; }
            Counters.CountUpload(r.ByteLength);
            var staging = NewBuffer(api, r.ByteLength, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            void* p;
            Check(api.vkMapMemory(staging.Item2, 0, (ulong)r.ByteLength, 0, &p), "vkMapMemory");
            Scene3DPatch.Source(scene, r).CopyTo(new Span<byte>(p, r.ByteLength));
            api.vkUnmapMemory(staging.Item2);
            var dst = target.Buf;
            ulong offset = (ulong)r.ByteOffset, size = (ulong)r.ByteLength;
            OneShot(api, cb =>
            {
                var region = new VkBufferCopy { dstOffset = offset, size = size };
                api.vkCmdCopyBuffer(cb, staging.Item1, dst, 1, &region);
            });
            api.vkDestroyBuffer(staging.Item1, null);
            api.vkFreeMemory(staging.Item2, null);
        }
    }

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines)
    {
        var api = Api;
        int i = slot - Scene3DBuffer.Overlay0;
        Free(api, ref _overlays[i]);
        fixed (Scene3DVertex* p = lines) _overlays[i] = Upload(api, p, lines.Length * Scene3DVertex.Stride, VkBufferUsageFlags.VertexBuffer);
    }

    public override void UploadField(FieldVertex[] vertices)
    {
        var api = Api;
        Free(api, ref _field);
        fixed (FieldVertex* p = vertices) _field = Upload(api, p, vertices.Length * FieldVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        _fieldCount = vertices.Length;
    }

    /// <summary>A buffer may still be read by a frame in flight, so replacing one waits for the GPU —
    /// once per generation, never per frame.</summary>
    private void Free(VkDeviceApi api, ref (VkBuffer Buf, VkDeviceMemory Mem) b)
    {
        if (b.Buf.Handle == 0) return;
        api.vkDeviceWaitIdle();
        api.vkDestroyBuffer(b.Buf, null);
        api.vkFreeMemory(b.Mem, null);
        b = default;
    }

    /// <summary>Device-local geometry through a staging buffer; every byte counted once.</summary>
    private (VkBuffer, VkDeviceMemory) Upload(VkDeviceApi api, void* data, int length, VkBufferUsageFlags usage)
    {
        if (length == 0) return default;
        Counters.CountUpload(length);
        var staging = NewBuffer(api, length, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* p;
        Check(api.vkMapMemory(staging.Item2, 0, (ulong)length, 0, &p), "vkMapMemory");
        Buffer.MemoryCopy(data, p, length, length);
        api.vkUnmapMemory(staging.Item2);
        var dst = NewBuffer(api, length, usage | VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.DeviceLocal);
        OneShot(api, cb =>
        {
            var region = new VkBufferCopy { size = (ulong)length };
            api.vkCmdCopyBuffer(cb, staging.Item1, dst.Item1, 1, &region);
        });
        api.vkDestroyBuffer(staging.Item1, null);
        api.vkFreeMemory(staging.Item2, null);
        return dst;
    }

    private void OneShot(VkDeviceApi api, Action<VkCommandBuffer> record)
    {
        var ai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer cb;
        Check(api.vkAllocateCommandBuffers(&ai, &cb), "vkAllocateCommandBuffers");
        try
        {
            var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
            Check(api.vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");
            record(cb);
            Check(api.vkEndCommandBuffer(cb), "vkEndCommandBuffer");
            var si = new VkSubmitInfo { commandBufferCount = 1, pCommandBuffers = &cb };
            Check(api.vkQueueSubmit(_queue, 1, &si, VkFence.Null), "vkQueueSubmit");
            Check(api.vkQueueWaitIdle(_queue), "vkQueueWaitIdle");
        }
        finally { api.vkFreeCommandBuffers(_cmdPool, 1, &cb); }
    }

    /// <summary>brief-em3d-48 — room for <paramref name="slots"/> transforms per frame slot: when the plan outgrows the
    /// ring (an array's elements), the device is idled, the ring re-made at least twice as large, and binding 1 pointed at
    /// it. Nothing is in flight then, so no descriptor is rewritten under a command buffer.</summary>
    private void EnsureTransformRing(VkDeviceApi api, int slots)
    {
        if (slots <= _transformSlots) return;
        Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
        api.vkUnmapMemory(_tb.Mem);
        Free(api, _tb);
        _transformSlots = Math.Max(slots, 2 * _transformSlots);
        _tb = NewBuffer(api, Ring * _transformSlots * TransformStride, VkBufferUsageFlags.UniformBuffer,
                        VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* tm;
        Check(api.vkMapMemory(_tb.Mem, 0, VK_WHOLE_SIZE, 0, &tm), "vkMapMemory");
        _tMapped = (byte*)tm;
        var tbi = new VkDescriptorBufferInfo { buffer = _tb.Buf, offset = 0, range = Scene3DFramePlan.TransformBytesPerDraw };
        var write = new VkWriteDescriptorSet { dstSet = _set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &tbi };
        api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private static void Free(VkDeviceApi api, (VkBuffer Buf, VkDeviceMemory Mem) b)
    {
        if (b.Buf.Handle == 0) return;
        api.vkDestroyBuffer(b.Buf, null);
        api.vkFreeMemory(b.Mem, null);
    }

    // ── presentation ────────────────────────────────────────────────────────────────────────

    public override string? CheckInterop(ICompositionGpuInterop interop)
    {
        string images = string.Join(", ", interop.SupportedImageHandleTypes), sems = string.Join(", ", interop.SupportedSemaphoreTypes);
        if (!interop.SupportedImageHandleTypes.Contains(FdHandle) || !interop.SupportedSemaphoreTypes.Contains(FdSemaphore))
            return $"the compositor cannot import a Vulkan image and semaphores by POSIX file descriptor (it offered images [{images}], " +
                   $"semaphores [{sems}]; under GLX that needs the GL driver's GL_EXT_memory_object_fd and GL_EXT_semaphore_fd)";
        var caps = interop.GetSynchronizationCapabilities(FdHandle);
        if (!caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Semaphores))
            return $"the compositor's Vulkan-image synchronisation [{caps}] offers no semaphores";
        var uuid = interop.DeviceUuid;
        if (_api is null) CreateDevice(uuid);
        else if (uuid is { Length: 16 } && !uuid.AsSpan().SequenceEqual(_deviceUuid))
            return $"the compositor moved to GPU {Convert.ToHexString(uuid)} after the 3D view's device was made on another";
        if (!_canExport)
            return $"{_description} cannot export memory and semaphores as file descriptors (VK_KHR_external_memory_fd / VK_KHR_external_semaphore_fd)";
        return null;
    }

    public override void CreateImages(ICompositionGpuInterop interop, int width, int height, int count)
    {
        ReleaseImages();
        var api = Api;
        _images = new Image[count];
        for (int i = 0; i < count; i++)
        {
            var im = NewSwapImage(api, width, height, export: true);
            var gi = new VkMemoryGetFdInfoKHR { memory = im.Color.Memory, handleType = VkExternalMemoryHandleTypeFlags.OpaqueFD };
            int fd;
            Check(api.vkGetMemoryFdKHR(&gi, &fd), "vkGetMemoryFdKHR");
            if (fd < 0) throw new Viewer3DPresentFault("vkGetMemoryFdKHR returned no file descriptor for the 3D view's image.");
            im.Imported = interop.ImportImage(new PlatformHandle(fd, FdHandle), new PlatformGraphicsExternalImageProperties
            {
                Width = width, Height = height, Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                MemorySize = im.Color.MemorySize, MemoryOffset = 0, TopLeftOrigin = true,
            });
            im.Ready = NewExportableSemaphore(api);
            im.Released = NewExportableSemaphore(api);
            im.ReadyImported = interop.ImportSemaphore(new PlatformHandle(SemaphoreFd(api, im.Ready), FdSemaphore));
            im.ReleasedImported = interop.ImportSemaphore(new PlatformHandle(SemaphoreFd(api, im.Released), FdSemaphore));
            _images[i] = im;
        }
    }

    /// <summary>The headless path: <paramref name="count"/> images no compositor imports, rendered and
    /// read back with <see cref="ReadRgba"/>. What a later headless <c>render</c> of a 3D view, and the
    /// backend's own harness, draw into.</summary>
    internal void CreateOffscreenImages(int width, int height, int count)
    {
        ReleaseImages();
        var api = Api;
        _images = new Image[count];
        for (int i = 0; i < count; i++) _images[i] = NewSwapImage(api, width, height, export: false);
    }

    private Image NewSwapImage(VkDeviceApi api, int w, int h, bool export)
    {
        var im = new Image
        {
            Width = w, Height = h,
            Color = NewImage(api, w, h, ColorFormat, TargetUsage, VkImageAspectFlags.Color, export),
            Depth = NewImage(api, w, h, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, export: false),
        };
        im.Framebuffer = Framebuffer(api, _rpColor, [im.Color.View, im.Depth.View], w, h);
        return im;
    }

    private static VkSemaphore NewExportableSemaphore(VkDeviceApi api)
    {
        var esi = new VkExportSemaphoreCreateInfo { handleTypes = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        var sci = new VkSemaphoreCreateInfo { pNext = &esi };
        VkSemaphore s;
        Check(api.vkCreateSemaphore(&sci, null, &s), "vkCreateSemaphore");
        return s;
    }

    private static int SemaphoreFd(VkDeviceApi api, VkSemaphore s)
    {
        var gi = new VkSemaphoreGetFdInfoKHR { semaphore = s, handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        int fd;
        Check(api.vkGetSemaphoreFdKHR(&gi, &fd), "vkGetSemaphoreFdKHR");
        if (fd < 0) throw new Viewer3DPresentFault("vkGetSemaphoreFdKHR returned no file descriptor.");
        return fd;
    }

    public override void ReleaseImages()
    {
        if (_images.Length == 0 || _api is not { } api) { _images = []; return; }
        api.vkDeviceWaitIdle();
        // No wait on the compositor's pending update: this runs on the UI thread, and that update
        // completes only after the UI thread commits its batch — a wait here always timed out, stalling
        // every resize. Our side is idle (above), and the compositor's imports hold their own
        // references to the memory and semaphore payloads.
        foreach (var im in _images)
        {
            Dispose(im.Imported); Dispose(im.ReadyImported); Dispose(im.ReleasedImported);
            api.vkDestroyFramebuffer(im.Framebuffer, null);
            Destroy(api, im.Color); Destroy(api, im.Depth);
            if (im.Ready.Handle != 0) api.vkDestroySemaphore(im.Ready, null);
            if (im.Released.Handle != 0) api.vkDestroySemaphore(im.Released, null);
        }
        _images = [];
    }

    private static void Dispose(object? o)
    {
        if (o is IAsyncDisposable ad) _ = ad.DisposeAsync();
        else if (o is IDisposable d) d.Dispose();
    }

    public override bool WaitReusable(int image, int timeoutMs)
    {
        var im = _images[image];
        return !im.ReleaseOwed || im.Pending is not { IsCompleted: false } t || t.Wait(timeoutMs);
    }

    public override void Present(CompositionDrawingSurface surface, int image, ulong frame)
    {
        var im = _images[image];
        if (im.Imported is null) throw new Viewer3DPresentFault("The 3D view's image was never imported by the compositor.");
        im.Pending = surface.UpdateWithSemaphoresAsync(im.Imported, im.ReadyImported!, im.ReleasedImported!);
        im.ReleaseOwed = true;
        im.ReadySignalled = false;
    }

    // ── the frame ───────────────────────────────────────────────────────────────────────────

    public override void Render(int image, Scene3DFramePlan plan, ulong frame) => RenderTo(_images[image], plan, image);

    /// <summary>brief-em3d-29 R-em3d29-5 — the plan drawn into an image of its own (no semaphores, never
    /// imported) and read back: the same pipelines and buffers as the view, the swapchain untouched.</summary>
    public override byte[] RenderPixels(Scene3DFramePlan plan)
    {
        var api = Api;
        var im = NewSwapImage(api, plan.Width, plan.Height, export: false);
        try
        {
            RenderTo(im, plan, -1);
            Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
            return ReadRgba(im);
        }
        finally
        {
            api.vkDeviceWaitIdle();
            api.vkDestroyFramebuffer(im.Framebuffer, null);
            Destroy(api, im.Color); Destroy(api, im.Depth);
        }
    }

    private void RenderTo(Image im, Scene3DFramePlan plan, int image)
    {
        var api = Api;
        VkSemaphore wait = default, signal = default;
        if (im.ReleaseOwed)
        {
            // a binary semaphore may be waited only once its signal is submitted: the update must be done
            if (im.Pending is { IsCompleted: false })
                throw new Viewer3DPresentFault($"The compositor has not finished with the 3D view's image {image}, so its release cannot be waited for.");
            if (im.Pending is { IsFaulted: true } f)
                throw new Viewer3DPresentFault($"The compositor's update of the 3D view's image {image} failed: {f.Exception?.GetBaseException().Message}");
            // Cancelled: the compositor never took the image, so the release semaphore will never be
            // signalled and waiting on it would stall the GPU queue for good.
            if (im.Pending is { IsCanceled: true })
                throw new Viewer3DPresentFault($"The compositor cancelled its update of the 3D view's image {image}.");
            wait = im.Released;
            im.ReleaseOwed = false;
        }
        if (im.Ready.Handle != 0 && !im.ReadySignalled) signal = im.Ready;

        int f0 = (int)(_frame % Ring);
        CollectPicks(api);
        if (_submitted[f0])
        {
            var fence = _fence[f0];
            Check(api.vkWaitForFences(1, &fence, true, FenceTimeoutNs), "vkWaitForFences");
            ReadPick(f0);
            Check(api.vkResetFences(1, &fence), "vkResetFences");
            _submitted[f0] = false;
        }
        var cb = _cmd[f0];
        Check(api.vkResetCommandBuffer(cb, 0), "vkResetCommandBuffer");
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(api.vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");
        int draws = 0;
        var clears = stackalloc VkClearValue[3];
        ulong zero = 0;
        var set = _set;
        // brief-em3d-46 — this frame slot's transforms: slot 0 the identity, then (brief 48) each array element's and its
        // box's, then a preview's copies.
        EnsureTransformRing(api, plan.TransformCount);
        uint tBase = (uint)(f0 * _transformSlots * TransformStride);
        int tCount = Math.Min(plan.TransformCount, _transformSlots);
        fixed (float* xf = plan.Transforms)
            for (int k = 0; k < tCount; k++)
                Buffer.MemoryCopy(xf + Scene3DFramePlan.TransformFloats * k, _tMapped + tBase + k * TransformStride, TransformStride,
                                  Scene3DFramePlan.TransformBytesPerDraw);
        Counters.CountUniform((long)tCount * Scene3DFramePlan.TransformBytesPerDraw);
        var offs = stackalloc uint[2];

        if (plan.Pick && plan.PickDrawCount > 0 && _vb.Buf.Handle != 0 && _ib.Buf.Handle != 0 && !_pickPending[f0])
        {
            uint off = (uint)(f0 * 2 * UniformStride);
            fixed (float* pu = plan.PickUniforms) Buffer.MemoryCopy(pu, _uMapped + off, Scene3DFramePlan.UniformBytes, Scene3DFramePlan.UniformBytes);
            Counters.CountUniform(Scene3DFramePlan.UniformBytes);
            clears[0] = new VkClearValue(new VkClearColorValue(0u, 0u, 0u, 0u));
            clears[1] = new VkClearValue(0f, 0f, 0f, 0f);
            clears[2] = new VkClearValue(1f, 0u);
            var rbi = new VkRenderPassBeginInfo { renderPass = _rpPick, framebuffer = _pickFb, renderArea = new VkRect2D(0, 0, 1, 1), clearValueCount = 3, pClearValues = clears };
            api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
            SetViewport(api, cb, 1, 1);
            api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pPick);
            offs[0] = off; offs[1] = tBase;
            api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
            var vb = _vb.Buf;
            api.vkCmdBindVertexBuffers(cb, 0, 1, &vb, &zero);
            api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
            int pickTransform = 0;
            var pickTie = Scene3DDepthTie.None;
            SetTie(api, cb, pickTie);
            for (int i = 0; i < plan.PickDrawCount; i++)
            {
                ref var d = ref plan.PickDraws[i];
                if (d.Tie != pickTie) SetTie(api, cb, pickTie = d.Tie);
                // brief-em3d-48 — an array element's pick draw: its translation and id offset.
                if (d.Transform != pickTransform && d.Transform < tCount)
                {
                    pickTransform = d.Transform;
                    offs[1] = tBase + (uint)(pickTransform * TransformStride);
                    api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
                }
                api.vkCmdDrawIndexed(cb, (uint)d.Count, 1, (uint)d.First, 0, 0); draws++;
            }
            api.vkCmdEndRenderPass(cb);
            CopyTexel(api, cb, _pickId.Image, _pickBuf[f0].Buf, 0);
            CopyTexel(api, cb, _pickPos.Image, _pickBuf[f0].Buf, 16);
            var hb = new VkBufferMemoryBarrier
            {
                srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.HostRead,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                buffer = _pickBuf[f0].Buf, offset = 0, size = VK_WHOLE_SIZE,
            };
            api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, 0, 0, null, 1, &hb, 0, null);
            _pickPending[f0] = true;
            _pickFrame[f0] = Counters.FrameIndex;
        }

        {
            uint off = (uint)((f0 * 2 + 1) * UniformStride);
            fixed (float* u = plan.Uniforms) Buffer.MemoryCopy(u, _uMapped + off, Scene3DFramePlan.UniformBytes, Scene3DFramePlan.UniformBytes);
            Counters.CountUniform(Scene3DFramePlan.UniformBytes);
            var (r, g, b) = plan.Clear;
            clears[0] = new VkClearValue(r, g, b, 1f);
            clears[1] = new VkClearValue(1f, 0u);
            var rbi = new VkRenderPassBeginInfo
            {
                renderPass = _rpColor, framebuffer = im.Framebuffer, renderArea = new VkRect2D(0, 0, (uint)im.Width, (uint)im.Height),
                clearValueCount = 2, pClearValues = clears,
            };
            api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
            SetViewport(api, cb, im.Width, im.Height);
            offs[0] = off; offs[1] = tBase;
            api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
            int transform = 0;
            Scene3DPipeline state = (Scene3DPipeline)(-1);
            var tie = Scene3DDepthTie.None;
            SetTie(api, cb, tie);
            Scene3DBuffer bound = (Scene3DBuffer)(-1);
            for (int i = 0; i < plan.DrawCount; i++)
            {
                ref var d = ref plan.Draws[i];
                var buf = d.Buffer switch
                {
                    Scene3DBuffer.Scene => _vb.Buf, Scene3DBuffer.SceneLines => _lines.Buf, Scene3DBuffer.Field => _field.Buf,
                    Scene3DBuffer.Image => _imageVb.Buf,
                    Scene3DBuffer.Overlay0 => _overlays[0].Buf, Scene3DBuffer.Overlay1 => _overlays[1].Buf, _ => _overlays[2].Buf,
                };
                bool lines = d.Pipeline is Scene3DPipeline.Lines or Scene3DPipeline.Edges, field = d.Pipeline == Scene3DPipeline.Field;
                bool grid = d.Pipeline == Scene3DPipeline.Grid;
                // brief-em3d-101 — an image draw: not indexed; its texture's set at set 1.
                bool isImage = d.Pipeline is Scene3DPipeline.Image or Scene3DPipeline.ImageTranslucent;
                var bound1 = _textures.Bound;
                if (isImage && (d.Texture < 0 || d.Texture >= bound1.Length || bound1[d.Texture] is null)) continue;
                if (!grid && (buf.Handle == 0 || (!lines && !field && !isImage && _ib.Buf.Handle == 0))) continue;
                if (field && d.First + d.Count > _fieldCount) continue;
                if (d.Tie != tie) SetTie(api, cb, tie = d.Tie);
                if (d.Pipeline != state)
                {
                    state = d.Pipeline;
                    api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, state switch
                    {
                        Scene3DPipeline.Translucent => _pTrans, Scene3DPipeline.Lines => _pLines,
                        Scene3DPipeline.Field => _pField, Scene3DPipeline.Edges => _pEdges,
                        Scene3DPipeline.OnTop => _pTop, Scene3DPipeline.Grid => _pGrid,
                        Scene3DPipeline.Image => _pImage, Scene3DPipeline.ImageTranslucent => _pImageTrans, _ => _pOpaque,
                    });
                }
                if (isImage)
                {
                    var texSet = bound1[d.Texture]!.Set;
                    api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 1, 1, &texSet, 0, null);
                }
                if (grid)
                {
                    api.vkCmdDraw(cb, 6, 1, 0, 0);
                    draws++;
                    continue;
                }
                if (d.Transform != transform && d.Transform < tCount)
                {
                    // brief-em3d-46 — a drag's preview: the same set, re-offset to this draw's transform.
                    transform = d.Transform;
                    offs[1] = tBase + (uint)(transform * TransformStride);
                    api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
                }
                if (d.Buffer != bound)
                {
                    bound = d.Buffer;
                    api.vkCmdBindVertexBuffers(cb, 0, 1, &buf, &zero);
                    if (!lines && !field && !isImage) api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
                }
                if (lines || field || isImage) api.vkCmdDraw(cb, (uint)d.Count, 1, (uint)d.First, 0);
                else api.vkCmdDrawIndexed(cb, (uint)d.Count, 1, (uint)d.First, 0, 0);
                draws++;
            }
            api.vkCmdEndRenderPass(cb);
        }
        Check(api.vkEndCommandBuffer(cb), "vkEndCommandBuffer");

        var waitStage = VkPipelineStageFlags.ColorAttachmentOutput;
        var si = new VkSubmitInfo
        {
            waitSemaphoreCount = wait.Handle != 0 ? 1u : 0u, pWaitSemaphores = &wait, pWaitDstStageMask = &waitStage,
            commandBufferCount = 1, pCommandBuffers = &cb,
            signalSemaphoreCount = signal.Handle != 0 ? 1u : 0u, pSignalSemaphores = &signal,
        };
        Check(api.vkQueueSubmit(_queue, 1, &si, _fence[f0]), "vkQueueSubmit");
        if (signal.Handle != 0) im.ReadySignalled = true;
        _submitted[f0] = true;
        _frame++;
        DrawCallsLastFrame = draws;
    }

    private static void SetViewport(VkDeviceApi api, VkCommandBuffer cb, int w, int h)
    {
        var view = new VkViewport { x = 0, y = 0, width = w, height = h, minDepth = 0, maxDepth = 1 };
        var sc = new VkRect2D(0, 0, (uint)w, (uint)h);
        api.vkCmdSetViewport(cb, 0, 1, &view);
        api.vkCmdSetScissor(cb, 0, 1, &sc);
    }

    private static void CopyTexel(VkDeviceApi api, VkCommandBuffer cb, VkImage img, VkBuffer buf, ulong offset)
    {
        var region = new VkBufferImageCopy
        {
            bufferOffset = offset,
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
            imageExtent = new VkExtent3D { width = 1, height = 1, depth = 1 },
        };
        api.vkCmdCopyImageToBuffer(cb, img, VkImageLayout.TransferSrcOptimal, buf, 1, &region);
    }

    /// <summary>Non-blocking: a slot's texels are read only once its fence has signalled.</summary>
    private void CollectPicks(VkDeviceApi api)
    {
        for (int k = 1; k <= Ring; k++)
        {
            int s = (int)((_frame + k) % Ring);   // oldest first
            if (!_pickPending[s] || !_submitted[s]) continue;
            if (api.vkGetFenceStatus(_fence[s]) != VkResult.Success) continue;
            ReadPick(s);
        }
    }

    private void ReadPick(int s)
    {
        if (!_pickPending[s]) return;
        byte* p = _pickMapped[s];
        PickedId = *(uint*)p;
        PickedFace = ((uint*)p)[1];
        float* w = (float*)(p + 16);
        PickedPoint = new Vector3(w[0], w[1], w[2]);
        PickedSomething = w[3] > 0.5f;
        _pickPending[s] = false;
        Counters.PickResolved(_pickFrame[s]);
    }

    /// <summary>The headless path: waits for the GPU and reads image <paramref name="image"/> (left in
    /// TRANSFER_SRC_OPTIMAL by its frame) as RGBA rows, top row first.</summary>
    internal byte[] ReadRgba(int image) => ReadRgba(_images[image]);

    private byte[] ReadRgba(Image im)
    {
        var api = Api;
        Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
        for (int s = 0; s < Ring; s++) ReadPick(s);
        long size = (long)im.Width * im.Height * 4;
        var buf = NewBuffer(api, size, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        OneShot(api, cb =>
        {
            var region = new VkBufferImageCopy
            {
                imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
                imageExtent = new VkExtent3D { width = (uint)im.Width, height = (uint)im.Height, depth = 1 },
            };
            api.vkCmdCopyImageToBuffer(cb, im.Color.Image, VkImageLayout.TransferSrcOptimal, buf.Item1, 1, &region);
        });
        void* p;
        Check(api.vkMapMemory(buf.Item2, 0, (ulong)size, 0, &p), "vkMapMemory");
        var px = new byte[size];
        Marshal.Copy((nint)p, px, 0, px.Length);
        api.vkUnmapMemory(buf.Item2);
        Free(api, buf);
        return px;
    }

    public override void Dispose()
    {
        if (_api is not { } api)
        {
            _vi?.vkDestroyInstance(null);      // a device that failed to come up after its instance did
            _vi = null;
            return;
        }
        api.vkDeviceWaitIdle();
        ReleaseImages();
        Free(api, _vb); Free(api, _ib); Free(api, _lines); Free(api, _field); Free(api, _imageVb);
        _textures.Clear(ReleaseTexture);
        foreach (var o in _overlays) Free(api, o);
        foreach (var b in _pickBuf) Free(api, b);
        Free(api, _ub);
        Free(api, _tb);
        Destroy(api, _pickId); Destroy(api, _pickPos); Destroy(api, _pickDepth);
        if (_pickFb.Handle != 0) api.vkDestroyFramebuffer(_pickFb, null);
        foreach (var f in _fence) if (f.Handle != 0) api.vkDestroyFence(f, null);
        api.vkDestroyPipeline(_pOpaque, null); api.vkDestroyPipeline(_pTrans, null);

        api.vkDestroyPipeline(_pLines, null); api.vkDestroyPipeline(_pPick, null); api.vkDestroyPipeline(_pField, null);
        api.vkDestroyPipeline(_pEdges, null); api.vkDestroyPipeline(_pTop, null); api.vkDestroyPipeline(_pGrid, null);
        api.vkDestroyPipeline(_pImage, null); api.vkDestroyPipeline(_pImageTrans, null);
        api.vkDestroySampler(_sampler, null); api.vkDestroyDescriptorSetLayout(_imageSetLayout, null);
        api.vkDestroyPipelineLayout(_layout, null); api.vkDestroyDescriptorPool(_pool, null);
        api.vkDestroyDescriptorSetLayout(_setLayout, null); api.vkDestroyShaderModule(_module, null);
        api.vkDestroyRenderPass(_rpColor, null); api.vkDestroyRenderPass(_rpPick, null);
        api.vkDestroyCommandPool(_cmdPool, null);
        api.vkDestroyDevice(null);
        _vi?.vkDestroyInstance(null);
        _api = null;
    }
}
