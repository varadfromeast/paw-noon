using System;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using PawNoon.Core;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace PawNoon.Runtime.Net
{
    /// <summary>What the native Apple / Google sheet returned.</summary>
    public struct NativeSignInResult
    {
        public bool Ok;
        public bool Cancelled;
        public string Provider;
        /// <summary>The OpenID Connect ID token (a JWT) to hand to Supabase.</summary>
        public string IdToken;
        /// <summary>Apple only: the one-time authorization code (kept for a future token revoke on delete).</summary>
        public string AuthCode;
        /// <summary>Short machine code on failure ("unavailable", "no_token", "apple_1001", ...).</summary>
        public string Error;

        public static NativeSignInResult Fail(string provider, string error, bool cancelled)
        {
            return new NativeSignInResult { Ok = false, Cancelled = cancelled, Provider = provider, Error = error };
        }

        /// <summary>Reads the JSON the iOS plugin sends through UnitySendMessage.</summary>
        public static NativeSignInResult FromJson(string json)
        {
            var o = PnJson.ParseObject(json);
            var r = new NativeSignInResult();
            if (o == null) { r.Error = "bad_native_json"; return r; }
            r.Provider = PnJson.Str(o, "provider", "");
            r.IdToken = PnJson.Str(o, "idToken", null);
            r.AuthCode = PnJson.Str(o, "authCode", null);
            r.Cancelled = PnJson.Bool(o, "cancelled", false);
            r.Error = PnJson.Str(o, "error", null);
            r.Ok = PnJson.Bool(o, "ok", false) && !string.IsNullOrEmpty(r.IdToken);
            if (!r.Ok && string.IsNullOrEmpty(r.Error)) r.Error = r.Cancelled ? "cancelled" : "no_token";
            return r;
        }
    }

    /// <summary>
    /// The seam between the game and the platform's sign-in sheet. One request at a time; the callback runs exactly
    /// once on the main thread. Implementations: iOS (AuthenticationServices via Plugins/iOS/PawSignIn.mm), Android
    /// (Credential Manager via AndroidJavaObject), Editor (mock token), and "unavailable" everywhere else.
    /// </summary>
    public interface INativeSignIn
    {
        /// <summary>"apple" or "google" can be offered on this device.</summary>
        bool Supports(string provider);
        bool Busy { get; }
        /// <summary><paramref name="nonceSha256Hex"/>: SHA-256 (lower-case hex) of the raw nonce Supabase gets.</summary>
        void Begin(string provider, string nonceSha256Hex, Action<NativeSignInResult> done);
        /// <summary>True for the Editor mock (the token is fake; the link is simulated, never sent to a server).</summary>
        bool IsMock { get; }
    }

    /// <summary>Raw nonce for Supabase, its SHA-256 for Apple / Google (replay protection for the ID token).</summary>
    public static class Nonce
    {
        public static string NewRaw()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Hex(b);
        }

        public static string Sha256Hex(string raw)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(raw ?? "")));
        }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    public static class NativeSignIn
    {
        /// <summary>The implementation for this build. <paramref name="forceMock"/>: always the Editor-style mock.</summary>
        public static INativeSignIn Create(AuthConfig cfg, bool forceMock)
        {
            if (forceMock || Application.isEditor) return new MockNativeSignIn(cfg);
#if UNITY_IOS && !UNITY_EDITOR
            return new IosNativeSignIn(cfg);
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidNativeSignIn(cfg);
#else
            return new UnavailableNativeSignIn();
#endif
        }
    }

    /// <summary>
    /// Main-thread mailbox for native answers. Its GameObject is named "PawSignIn" so the iOS plugin can reach it with
    /// <c>UnitySendMessage("PawSignIn", "OnNativeResult", json)</c>. Android callbacks arrive on a binder thread and are
    /// handed over through <see cref="Post"/>; Update picks them up (one volatile read per frame, no allocation).
    /// </summary>
    public sealed class NativeSignInHost : MonoBehaviour
    {
        public const string ObjectName = "PawSignIn";
        private static NativeSignInHost instance;

        private readonly object gate = new object();
        private Action<NativeSignInResult> waiting;
        private volatile bool posted;
        private NativeSignInResult postedResult;

        public static NativeSignInHost Ensure()
        {
            if (instance != null) return instance;
            var go = new GameObject(ObjectName);   // root object: UnitySendMessage finds it by name
            DontDestroyOnLoad(go);
            instance = go.AddComponent<NativeSignInHost>();
            return instance;
        }

        public bool Waiting { get { return waiting != null; } }

        public void Expect(Action<NativeSignInResult> done)
        {
            lock (gate)
            {
                waiting = done;
                posted = false;
            }
        }

        /// <summary>Any thread.</summary>
        public void Post(NativeSignInResult r)
        {
            lock (gate)
            {
                postedResult = r;
                posted = true;
            }
        }

        /// <summary>iOS plugin -> Unity (UnitySendMessage, main thread). Called by name only: [Preserve] keeps it under
        /// IL2CPP managed stripping (review pass 2).</summary>
        [UnityEngine.Scripting.Preserve]
        public void OnNativeResult(string json)
        {
            Post(NativeSignInResult.FromJson(json));
        }

        private void Update()
        {
            if (!posted) return;
            Action<NativeSignInResult> cb;
            NativeSignInResult r;
            lock (gate)
            {
                posted = false;
                cb = waiting;
                waiting = null;
                r = postedResult;
            }
            if (cb == null) return;
            try { cb(r); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }

    /// <summary>Editor (and forced-mock) sign-in: after a short "sheet" it returns a fake token. Never sent to a server.</summary>
    public sealed class MockNativeSignIn : INativeSignIn
    {
        /// <summary>Next result: "ok" (default), "cancel", "fail". Reset to "ok" after each use (ReviewDirector).</summary>
        public static string NextOutcome = "ok";
        private const float SheetSeconds = 0.7f;
        private readonly AuthConfig cfg;
        private bool busy;

        public MockNativeSignIn(AuthConfig cfg) { this.cfg = cfg; }

        public bool IsMock { get { return true; } }
        public bool Busy { get { return busy; } }

        /// <summary>The Editor shows both buttons so the iOS layout can be reviewed on any build target.</summary>
        public bool Supports(string provider)
        {
            if (provider == "apple") return cfg.Apple;
            if (provider == "google") return cfg.Google;
            return false;
        }

        public void Begin(string provider, string nonceSha256Hex, Action<NativeSignInResult> done)
        {
            busy = true;
            NetHost.Run(Run(provider, nonceSha256Hex, done));
        }

        private IEnumerator Run(string provider, string nonce, Action<NativeSignInResult> done)
        {
            yield return new WaitForSecondsRealtime(SheetSeconds);
            string o = NextOutcome ?? "ok";
            NextOutcome = "ok";
            busy = false;
            if (o == "cancel") { done(NativeSignInResult.Fail(provider, "cancelled", true)); yield break; }
            if (o == "fail") { done(NativeSignInResult.Fail(provider, "mock_fail", false)); yield break; }
            done(new NativeSignInResult
            {
                Ok = true,
                Provider = provider,
                IdToken = "mock." + provider + "." + (nonce != null && nonce.Length >= 8 ? nonce.Substring(0, 8) : "nononce")
            });
        }
    }

    public sealed class UnavailableNativeSignIn : INativeSignIn
    {
        public bool IsMock { get { return false; } }
        public bool Busy { get { return false; } }
        public bool Supports(string provider) { return false; }

        public void Begin(string provider, string nonceSha256Hex, Action<NativeSignInResult> done)
        {
            done(NativeSignInResult.Fail(provider, "unavailable", false));
        }
    }

#if UNITY_IOS && !UNITY_EDITOR
    /// <summary>
    /// iOS: Plugins/iOS/PawSignIn.mm. Apple = ASAuthorizationAppleIDProvider (iOS 13+). Google = ASWebAuthenticationSession
    /// to accounts.google.com with the iOS OAuth client, PKCE (S256) and the hashed nonce, then the code is exchanged at
    /// oauth2.googleapis.com/token for the ID token (public client, no secret, no Google SDK). Results come back through
    /// UnitySendMessage("PawSignIn", "OnNativeResult", json).
    /// </summary>
    public sealed class IosNativeSignIn : INativeSignIn
    {
        [DllImport("__Internal")] private static extern int PawSignIn_AppleAvailable();
        [DllImport("__Internal")] private static extern void PawSignIn_Apple(string nonceSha256Hex);
        [DllImport("__Internal")] private static extern void PawSignIn_Google(string iosClientId, string nonceSha256Hex);

        private readonly AuthConfig cfg;
        private readonly NativeSignInHost host;

        public IosNativeSignIn(AuthConfig cfg)
        {
            this.cfg = cfg;
            host = NativeSignInHost.Ensure();
        }

        public bool IsMock { get { return false; } }
        public bool Busy { get { return host.Waiting; } }

        public bool Supports(string provider)
        {
            if (provider == "apple")
            {
                if (!cfg.Apple) return false;
                try { return PawSignIn_AppleAvailable() != 0; }
                catch (Exception) { return false; }
            }
            if (provider == "google") return cfg.GoogleIosReady;
            return false;
        }

        public void Begin(string provider, string nonceSha256Hex, Action<NativeSignInResult> done)
        {
            if (!Supports(provider)) { done(NativeSignInResult.Fail(provider, "unavailable", false)); return; }
            host.Expect(done);
            try
            {
                if (provider == "apple") PawSignIn_Apple(nonceSha256Hex);
                else PawSignIn_Google(cfg.GoogleIosClientId, nonceSha256Hex);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] native sign-in failed to start: " + e.Message);
                host.Post(NativeSignInResult.Fail(provider, "unavailable", false));
            }
        }
    }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>
    /// Android: Jetpack Credential Manager + "Sign in with Google" (GetSignInWithGoogleOption), called through
    /// AndroidJavaObject so the game compiles without the libraries. The Gradle lines that make it work are in
    /// Docs/ACCOUNTS.md; without them Begin answers "unavailable" (ClassNotFound is caught).
    /// </summary>
    public sealed class AndroidNativeSignIn : INativeSignIn
    {
        private const string GoogleIdType = "com.google.android.libraries.identity.googleid.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL";
        private readonly AuthConfig cfg;
        private readonly NativeSignInHost host;
        private int available = -1;

        public AndroidNativeSignIn(AuthConfig cfg)
        {
            this.cfg = cfg;
            host = NativeSignInHost.Ensure();
        }

        public bool IsMock { get { return false; } }
        public bool Busy { get { return host.Waiting; } }

        public bool Supports(string provider)
        {
            if (provider != "google" || !cfg.GoogleAndroidReady) return false;
            if (available < 0)
            {
                try
                {
                    using (new AndroidJavaClass("androidx.credentials.CredentialManager")) { }
                    using (new AndroidJavaClass("com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption")) { }
                    available = 1;
                }
                catch (Exception)
                {
                    available = 0;
                    Debug.LogWarning("[PawNoon] Credential Manager / googleid not in this build (Docs/ACCOUNTS.md, Gradle)");
                }
            }
            return available == 1;
        }

        public void Begin(string provider, string nonceSha256Hex, Action<NativeSignInResult> done)
        {
            if (!Supports(provider)) { done(NativeSignInResult.Fail(provider, "unavailable", false)); return; }
            host.Expect(done);
            try
            {
                var activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer").GetStatic<AndroidJavaObject>("currentActivity");
                var option = new AndroidJavaObject("com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption$Builder", cfg.GoogleWebClientId)
                    .Call<AndroidJavaObject>("setNonce", nonceSha256Hex)
                    .Call<AndroidJavaObject>("build");
                var request = new AndroidJavaObject("androidx.credentials.GetCredentialRequest$Builder")
                    .Call<AndroidJavaObject>("addCredentialOption", option)
                    .Call<AndroidJavaObject>("build");
                var manager = CredentialManager(activity);
                var cancel = new AndroidJavaObject("android.os.CancellationSignal");
                manager.Call("getCredentialAsync", activity, request, cancel, new DirectExecutor(), new Callback(host));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] Credential Manager failed to start: " + e.Message);
                host.Post(NativeSignInResult.Fail("google", "unavailable", false));
            }
        }

        private static AndroidJavaObject CredentialManager(AndroidJavaObject context)
        {
            using (var cls = new AndroidJavaClass("androidx.credentials.CredentialManager"))
            {
                try { return cls.CallStatic<AndroidJavaObject>("create", context); }
                catch (Exception)
                {
                    // Kotlin companion without @JvmStatic
                    var companion = cls.GetStatic<AndroidJavaObject>("Companion");
                    return companion.Call<AndroidJavaObject>("create", context);
                }
            }
        }

        /// <summary>java.util.concurrent.Executor that runs the callback on the calling (binder) thread.</summary>
        private sealed class DirectExecutor : AndroidJavaProxy
        {
            public DirectExecutor() : base("java.util.concurrent.Executor") { }
            // invoked from Java by name (AndroidJavaProxy reflection): keep it under managed stripping
            [UnityEngine.Scripting.Preserve]
            public void execute(AndroidJavaObject runnable) { runnable.Call("run"); }
        }

        /// <summary>androidx.credentials.CredentialManagerCallback&lt;GetCredentialResponse, GetCredentialException&gt;.</summary>
        private sealed class Callback : AndroidJavaProxy
        {
            private readonly NativeSignInHost host;
            public Callback(NativeSignInHost host) : base("androidx.credentials.CredentialManagerCallback") { this.host = host; }

            [UnityEngine.Scripting.Preserve]
            public void onResult(AndroidJavaObject response)
            {
                try
                {
                    var cred = response.Call<AndroidJavaObject>("getCredential");
                    string type = cred.Call<string>("getType");
                    if (type != GoogleIdType)
                    {
                        host.Post(NativeSignInResult.Fail("google", "unexpected_credential", false));
                        return;
                    }
                    var data = cred.Call<AndroidJavaObject>("getData");
                    AndroidJavaObject g;
                    using (var cls = new AndroidJavaClass("com.google.android.libraries.identity.googleid.GoogleIdTokenCredential"))
                    {
                        try { g = cls.CallStatic<AndroidJavaObject>("createFrom", data); }
                        catch (Exception) { g = cls.GetStatic<AndroidJavaObject>("Companion").Call<AndroidJavaObject>("createFrom", data); }
                    }
                    string token = g.Call<string>("getIdToken");
                    host.Post(string.IsNullOrEmpty(token)
                        ? NativeSignInResult.Fail("google", "no_token", false)
                        : new NativeSignInResult { Ok = true, Provider = "google", IdToken = token });
                }
                catch (Exception e)
                {
                    host.Post(NativeSignInResult.Fail("google", "bad_credential: " + e.Message, false));
                }
            }

            [UnityEngine.Scripting.Preserve]
            public void onError(AndroidJavaObject error)
            {
                string type = "";
                try { type = error.Call<string>("getType") ?? ""; } catch (Exception) { }
                bool cancelled = type.IndexOf("USER_CANCELED", StringComparison.Ordinal) >= 0;
                string code = cancelled ? "cancelled" : (type.IndexOf("NO_CREDENTIAL", StringComparison.Ordinal) >= 0 ? "no_google_account" : "credential_error");
                host.Post(NativeSignInResult.Fail("google", code, cancelled));
            }
        }
    }
#endif
}
