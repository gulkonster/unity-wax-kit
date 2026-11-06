using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.Video;

namespace UnityWaxKit
{
    /// <summary>
    /// Shows an NFT's image or video on a <see cref="RawImage"/> (uGUI): set an asset id, or a template id (the collection is
    /// optional and saves a lookup). Animated GIFs animate and videos play in <see cref="WaxMediaMode.Animated"/>; in
    /// <see cref="WaxMediaMode.Static"/> it shows one still image and nothing animates or plays sound. A failure shows the
    /// placeholder and calls <c>On Failed</c>; other NFTs are not affected. The component owns and frees its textures.
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    [AddComponentMenu("Unity WAX Kit/WAX NFT Media")]
    public sealed class WaxNftMedia : MonoBehaviour
    {
        /// <summary>Which mode the component uses.</summary>
        public enum ModeChoice
        {
            /// <summary><see cref="WaxMediaSettings.Mode"/>.</summary>
            Default,
            /// <summary>Animated.</summary>
            Animated,
            /// <summary>Still.</summary>
            Static,
        }

        [Tooltip("Asset id of the NFT (digits). Wins over the template id.")]
        [SerializeField] string assetId = "";
        [Tooltip("Template id (digits), used when no asset id is set.")]
        [SerializeField] string templateId = "";
        [Tooltip("Optional collection of the template: one read instead of a search by id.")]
        [SerializeField] string collectionName = "";
        [Tooltip("Animated (GIFs animate, videos play) or Static (one still image). Default: WaxKit.MediaSettings.Mode.")]
        [SerializeField] ModeChoice mode = ModeChoice.Default;
        [Tooltip("Load when the component starts.")]
        [SerializeField] bool loadOnStart = true;
        [Tooltip("Shown while loading and when the media cannot be shown. Empty: nothing.")]
        [SerializeField] Texture placeholder;
        [Tooltip("Called when the media is shown.")]
        [SerializeField] UnityEvent onLoaded = new UnityEvent();
        [Tooltip("Called with the reason when the media cannot be shown (the placeholder stays).")]
        [SerializeField] UnityEvent<string> onFailed = new UnityEvent<string>();

        RawImage target;
        CancellationTokenSource loading;
        WaxNftImage image;
        VideoPlayer video;
        RenderTexture videoTexture;
        int frame;
        float frameTime;
        bool loadStarted;
        bool reloadOnEnable;
        TaskCompletionSource<bool> videoStart;
        bool videoFirstFrameOnly;

        /// <summary>The asset id; set it, then call <see cref="Load"/>.</summary>
        public string AssetId { get => assetId; set => assetId = value ?? ""; }
        /// <summary>The template id.</summary>
        public string TemplateId { get => templateId; set => templateId = value ?? ""; }
        /// <summary>The template's collection (optional).</summary>
        public string CollectionName { get => collectionName; set => collectionName = value ?? ""; }
        /// <summary>The mode.</summary>
        public ModeChoice Mode { get => mode; set => mode = value; }
        /// <summary>The NFT's media references, after loading.</summary>
        public WaxNftMediaInfo Info { get; private set; }
        /// <summary>The image shown (null for a video). Owned by this component.</summary>
        public WaxNftImage Image => image;
        /// <summary>The reason of the last failure, or null.</summary>
        public string Error { get; private set; }
        /// <summary>True while loading.</summary>
        public bool IsLoading => loading != null;
        /// <summary>Called when the media is shown.</summary>
        public UnityEvent OnLoaded => onLoaded;
        /// <summary>Called with the reason when the media cannot be shown.</summary>
        public UnityEvent<string> OnFailed => onFailed;

        WaxMediaMode EffectiveMode => mode == ModeChoice.Animated ? WaxMediaMode.Animated
            : mode == ModeChoice.Static ? WaxMediaMode.Static
            : WaxKit.MediaSettings.Mode;

        void Awake() => target = GetComponent<RawImage>();

        void Start()
        {
            // A Show / ShowTemplate / Load call before Start (e.g. right after AddComponent) already started the load.
            if (loadOnStart && !loadStarted && (!string.IsNullOrWhiteSpace(assetId) || !string.IsNullOrWhiteSpace(templateId))) _ = Load();
        }

        // Disable releases the current video player (stopped, handlers removed, texture freed), so none of its late events
        // (error, frame, time limit) reach the component, now or after enable; enable then loads the media again once.
        void OnDisable()
        {
            var hadVideo = video != null; // read first: a cancelled start may release the player inside Cancel
            Cancel();
            if (hadVideo)
            {
                ShowTexture(placeholder); // before Release: the RawImage must not keep the freed video texture
                Release();
                reloadOnEnable = true;
            }
        }

        void OnEnable()
        {
            if (!reloadOnEnable) return;
            // Load sets loadStarted, so a Start in the same frame does not start a second load.
            _ = Load();
        }

        void OnDestroy()
        {
            Cancel();
            Release();
        }

        /// <summary>Shows this asset.</summary>
        public Task Show(string newAssetId)
        {
            assetId = newAssetId ?? "";
            return Load();
        }

        /// <summary>Shows this template (collection optional).</summary>
        public Task ShowTemplate(string newTemplateId, string collection = null)
        {
            assetId = "";
            templateId = newTemplateId ?? "";
            collectionName = collection ?? "";
            return Load();
        }

        /// <summary>Removes the media and shows the placeholder.</summary>
        public void Clear()
        {
            Cancel();
            Release();
            reloadOnEnable = false;
            Info = null;
            Error = null;
            ShowTexture(placeholder);
        }

        /// <summary>Loads and shows the media of <see cref="AssetId"/> (or <see cref="TemplateId"/>). Never throws: failures go to <see cref="OnFailed"/>.</summary>
        public async Task Load()
        {
            loadStarted = true;
            reloadOnEnable = false;
            Cancel();
            Release();
            Error = null;
            ShowTexture(placeholder);
            var cancel = loading = new CancellationTokenSource();
            var token = cancel.Token;
            try
            {
                var media = WaxKit.NftMedia;
                Info = !string.IsNullOrWhiteSpace(assetId)
                    ? await media.GetAssetMedia(assetId.Trim(), token)
                    : await media.GetTemplateMedia(templateId, collectionName, token);
                token.ThrowIfCancellationRequested();

                var mode = EffectiveMode;
                if (Info.HasVideo && mode == WaxMediaMode.Animated)
                {
                    await PlayVideo(await media.GetVideoUrl(Info.Video, token), false, token);
                }
                else
                {
                    var still = Info.HasVideo ? WaxNftMediaData.StillReferences(Info) : Info.Image == null ? new string[0] : new[] { Info.Image };
                    string reference = null;
                    foreach (var r in still) { reference = r; break; }
                    if (reference != null)
                    {
                        var loaded = await media.LoadImage(reference, mode, token);
                        if (token.IsCancellationRequested) { loaded.Dispose(); return; }
                        image = loaded;
                        frame = 0;
                        frameTime = 0;
                        ShowTexture(image.Texture);
                    }
                    else if (Info.HasVideo && WaxKit.MediaSettings.FirstFrameForVideo)
                    {
                        await PlayVideo(await media.GetVideoUrl(Info.Video, token), true, token);
                    }
                    else throw new WaxKitException(WaxErrorKind.NotSupported, "The NFT has no image to show.");
                }
                if (!token.IsCancellationRequested) onLoaded.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer load, disabled or cleared.
            }
            catch (Exception e)
            {
                if (token.IsCancellationRequested) return;
                ShowTexture(placeholder); // before Release: the RawImage must not keep the freed video texture
                Release();
                Error = e.Message;
                onFailed.Invoke(e.Message);
            }
            finally
            {
                if (loading == cancel) loading = null;
                cancel.Dispose();
            }
        }

        /// <summary>Start preparing / playing a video player. Tests replace them, as they have no real video.</summary>
        internal static Action<VideoPlayer> PrepareVideo = p => p.Prepare();
        internal static Action<VideoPlayer> PlayPrepared = p => p.Play();

        // The video has started when it is prepared AND its first frame is shown; only then is the load done (On Loaded).
        // Not started within VideoStartTimeoutSeconds, or a player error before that: the load fails. Events of a player that
        // is no longer this component's video (replaced, cleared, disabled, destroyed) are ignored.
        async Task PlayVideo(string url, bool firstFrameOnly, CancellationToken token)
        {
            var start = videoStart = new TaskCompletionSource<bool>();
            videoFirstFrameOnly = firstFrameOnly;
            video = gameObject.AddComponent<VideoPlayer>();
            video.playOnAwake = false;
            video.source = VideoSource.Url;
            video.url = url;
            video.isLooping = !firstFrameOnly;
            video.renderMode = VideoRenderMode.RenderTexture;
            video.audioOutputMode = WaxKit.MediaSettings.VideoAudio && !firstFrameOnly ? VideoAudioOutputMode.Direct : VideoAudioOutputMode.None;
            video.skipOnDrop = true;
            video.errorReceived += OnVideoError;
            video.prepareCompleted += OnVideoPrepared;
            video.frameReady += OnVideoFrame;
            var player = video;
            PrepareVideo(player);

            var seconds = WaxKit.MediaSettings.VideoStartTimeoutSeconds;
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (token.Register(() => start.TrySetCanceled()))
            {
                try
                {
                    var first = await Task.WhenAny(start.Task, Task.Delay(TimeSpan.FromSeconds(seconds), timer.Token));
                    timer.Cancel();
                    token.ThrowIfCancellationRequested();
                    if (first != start.Task) throw new WaxKitException(WaxErrorKind.Timeout, $"The video did not start in {seconds} s.");
                    await start.Task;
                }
                catch (OperationCanceledException)
                {
                    // Cancelled (e.g. disabled) while this player is still the component's video: release it, so none of
                    // its late events reach the component. A newer load or Clear has already released it otherwise.
                    if (ReferenceEquals(video, player)) Release();
                    throw;
                }
            }
        }

        // Reference check: a destroyed player compares equal to null in Unity.
        bool IsCurrent(VideoPlayer source) => video is object && ReferenceEquals(source, video);

        internal void OnVideoPrepared(VideoPlayer source)
        {
            if (!IsCurrent(source) || videoStart == null || videoStart.Task.IsCompleted) return;
            if (videoTexture == null)
                videoTexture = new RenderTexture((int)Math.Max(1, source.width), (int)Math.Max(1, source.height), 0) { name = "NFT video" };
            source.targetTexture = videoTexture;
            source.sendFrameReadyEvents = true;
            PlayPrepared(source);
        }

        internal void OnVideoFrame(VideoPlayer source, long frameIndex)
        {
            if (!IsCurrent(source) || videoStart == null || videoStart.Task.IsCompleted) return;
            source.sendFrameReadyEvents = false;
            // First-frame mode: one frame, then the player stays paused: nothing more is decoded and no sound plays.
            if (videoFirstFrameOnly) source.Pause();
            ShowTexture(videoTexture);
            videoStart.TrySetResult(true);
        }

        internal void OnVideoError(VideoPlayer source, string message)
        {
            if (!IsCurrent(source) || videoStart == null) return;
            var error = "The video cannot be played: " + message;
            // Before the start: the running load fails with it.
            if (!videoStart.Task.IsCompleted)
            {
                videoStart.TrySetException(new WaxKitException(WaxErrorKind.NotSupported, error));
                return;
            }
            // The start was cancelled or has already failed: that load is over, nothing more to report.
            if (videoStart.Task.Status != TaskStatus.RanToCompletion) return;
            // After the start: the same failure as any other (a load still finishing is cancelled, so On Loaded does not follow).
            Cancel();
            ShowTexture(placeholder);
            Release();
            Error = error;
            onFailed.Invoke(error);
        }

        void Update()
        {
            if (image == null || image.IsDisposed || !image.IsAnimated || EffectiveMode != WaxMediaMode.Animated) return;
            frameTime += Time.unscaledDeltaTime;
            var delay = Math.Max(0.02f, image.Delays[frame]);
            if (frameTime < delay) return;
            frameTime -= delay;
            if (frameTime > delay) frameTime = 0;
            frame = (frame + 1) % image.Frames.Count;
            ShowTexture(image.Frames[frame]);
        }

        void ShowTexture(Texture texture)
        {
            if (target == null) target = GetComponent<RawImage>();
            target.texture = texture;
            target.enabled = texture != null;
        }

        void Cancel()
        {
            var current = loading;
            loading = null;
            if (current != null && !current.IsCancellationRequested) current.Cancel();
        }

        void Release()
        {
            image?.Dispose();
            image = null;
            videoStart = null;
            if (video != null)
            {
                video.errorReceived -= OnVideoError;
                video.prepareCompleted -= OnVideoPrepared;
                video.frameReady -= OnVideoFrame;
                video.Stop();
                Free(video);
                video = null;
            }
            if (videoTexture != null)
            {
                videoTexture.Release();
                Free(videoTexture);
                videoTexture = null;
            }
        }

        static void Free(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
