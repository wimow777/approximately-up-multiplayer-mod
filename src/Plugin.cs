using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;
using HarmonyLib;
using Steamworks;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// PlayerLimitMod — aumenta el límite de 4 a MAX_PLAYERS jugadores
//
// [P1] SteamMatchmaking.CreateLobby         — lobby size 4→MAX al crear
// [P2] SteamMatchmaking.SetLobbyMemberLimit — refuerzo post-creación
// [P3] Netcore.CreateNetcoreClient (mem)    — CMP EAX,4 → CMP EAX,MAX
// [P4] Core.Refresh*AvailableComponents     — boost inventario componentes
// [P5] Core._availablePlayerColors          — extiende de 4 a MAX_PLAYERS colores
// ─────────────────────────────────────────────────────────────────────────────

namespace PlayerLimitMod
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BasePlugin
    {
        // MAX_PLAYERS ahora viene del config (no es const). ORIGINAL_LIMIT es fijo (el juego).
        public static int  MAX_PLAYERS    = 8;
        public const  int  ORIGINAL_LIMIT = 4;

        // Toggles configurables
        public static bool EnableSeatBoost    = true;
        public static bool EnablePlayerColors = true;
        public static bool EnableOverlay      = true;

        private const long SERVER_CHECK_RVA = 0xA9FE0B;
        // Core.Singleton.GetAvailableComponents(SCPrefab) — RVA en GameAssembly.dll
        private const long GET_AVAIL_RVA = 0xE4AFB0;

        public static BepInEx.Logging.ManualLogSource ModLog = null;

        public override void Load()
        {
            ModLog = Log;

            // ── Configuración (BepInEx/config/com.mods.approxup.playerlimit.cfg) ──
            var cfgMax = Config.Bind("General", "MaxPlayers", 8,
                "Número máximo de jugadores (y de sillas colocables). El juego original permite 4. Rango 4–32.");
            var cfgSeat = Config.Bind("Funciones", "BoostSillas", true,
                "Sube las sillas colocables al máximo de jugadores.");
            var cfgColors = Config.Bind("Funciones", "ColoresJugadores", true,
                "Agrega colores extra para que cada jugador tenga el suyo.");
            var cfgOverlay = Config.Bind("Funciones", "Overlay", true,
                "Muestra el contador de jugadores en pantalla (esquina superior izquierda).");

            MAX_PLAYERS        = Math.Max(ORIGINAL_LIMIT, Math.Min(cfgMax.Value, 32)); // clamp 4..32
            EnableSeatBoost    = cfgSeat.Value;
            EnablePlayerColors = cfgColors.Value;
            EnableOverlay      = cfgOverlay.Value;
            Log.LogInfo($"[PlayerLimitMod] Config: MaxPlayers={MAX_PLAYERS}, BoostSillas={EnableSeatBoost}, " +
                        $"Colores={EnablePlayerColors}, Overlay={EnableOverlay}");

            // Registrar el MonoBehaviour del overlay en el dominio IL2CPP (solo si está activo).
            if (EnableOverlay)
            {
                try
                {
                    Il2CppInterop.Runtime.Injection.ClassInjector
                        .RegisterTypeInIl2Cpp<PlayerCounterOverlay>();
                    Log.LogInfo("[PlayerLimitMod] Overlay registrado en IL2CPP.");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[PlayerLimitMod] No se pudo registrar overlay: {ex.Message}");
                }
            }

            try
            {
                new Harmony(PluginInfo.GUID).PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo("[PlayerLimitMod] Harmony patches aplicados.");
            }
            catch (Exception ex)
            {
                Log.LogError($"[PlayerLimitMod] Error Harmony: {ex.Message}");
            }
            PatchServerSideLimit();
            if (EnableSeatBoost) HookAvailableComponents();
        }

        // ── P4: hook nativo de Core.Singleton.GetAvailableComponents(SCPrefab) ──
        // El garage valida la colocación con: usados < disponibles. "disponibles"
        // vive en Core.Singleton._sharedComponentsAvailability / _private (mapas
        // UnsafeHashMap<SCPrefab,int>). La silla vale 4 ahí.
        //
        // GetAvailableComponents es un método de Core.Singleton (struct), así que en
        // el hook `self` ES el puntero al singleton → accedemos a sus mapas en
        // self+0x60 y self+0x68. Antes de calcular, ponemos SOLO la silla en 8.
        // El juego computa "8 - usados" solo → permite 8 sillas, nada más cambia.
        private static ulong _seatPrefab = 0;
        internal static bool SeatResolved = false;
        // Solo re-aplicamos la silla cuando el juego recalculó el mapa (Refresh).
        // Así el hook no recorre el mapa en cada llamada → no baja FPS en el garage.
        internal static volatile bool SeatDirty = false;

        [UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate int GetAvailDelegate(IntPtr self, ulong scPrefab);
        private static GetAvailDelegate _origGetAvail;
        private static INativeDetour _availDetour;

        private static int GetAvailHook(IntPtr self, ulong scPrefab)
        {
            try
            {
                // Solo recorre el mapa si el juego lo recalculó (SeatDirty). En el
                // resto de llamadas no hace nada → cero costo por frame.
                if (SeatDirty && SeatResolved && self != IntPtr.Zero)
                {
                    SetSeatInMap(self, 0x60, MAX_PLAYERS); // _sharedComponentsAvailability
                    SetSeatInMap(self, 0x68, MAX_PLAYERS); // _privateComponentsAvailability
                    SeatDirty = false;
                }
            }
            catch { }
            return _origGetAvail(self, scPrefab);
        }

        // Pone el valor de la silla en `value` dentro del UnsafeHashMap del singleton.
        // HashMapHelper<TKey>: 0x00 Ptr(valores), 0x08 Keys, 0x10 Next, 0x18 Buckets,
        //                      0x24 Capacity, 0x2C BucketCapacity, 0x38 SizeOfTValue
        private static void SetSeatInMap(IntPtr singletonPtr, long fieldOff, int value)
        {
            IntPtr map = Marshal.ReadIntPtr(new IntPtr(singletonPtr.ToInt64() + fieldOff));
            if (map == IntPtr.Zero) return;
            IntPtr basePtr    = Marshal.ReadIntPtr(new IntPtr(map.ToInt64() + 0x00));
            IntPtr keysPtr    = Marshal.ReadIntPtr(new IntPtr(map.ToInt64() + 0x08));
            IntPtr nextPtr    = Marshal.ReadIntPtr(new IntPtr(map.ToInt64() + 0x10));
            IntPtr bucketsPtr = Marshal.ReadIntPtr(new IntPtr(map.ToInt64() + 0x18));
            int capacity      = Marshal.ReadInt32(new IntPtr(map.ToInt64() + 0x24));
            int bucketCap     = Marshal.ReadInt32(new IntPtr(map.ToInt64() + 0x2C));
            int sizeOfVal     = Marshal.ReadInt32(new IntPtr(map.ToInt64() + 0x38));
            if (basePtr == IntPtr.Zero || keysPtr == IntPtr.Zero || nextPtr == IntPtr.Zero || bucketsPtr == IntPtr.Zero) return;
            if (capacity <= 0 || capacity > 200000 || bucketCap <= 0 || bucketCap > 200000 || sizeOfVal != 4) return;

            for (int b = 0; b < bucketCap; b++)
            {
                int idx = Marshal.ReadInt32(new IntPtr(bucketsPtr.ToInt64() + (long)b * 4));
                int guard = 0;
                while (idx >= 0 && idx < capacity && guard++ < capacity)
                {
                    ulong key = (ulong)Marshal.ReadInt64(new IntPtr(keysPtr.ToInt64() + (long)idx * 8));
                    if (key == _seatPrefab)
                    {
                        IntPtr valAddr = new IntPtr(basePtr.ToInt64() + (long)idx * sizeOfVal);
                        if (Marshal.ReadInt32(valAddr) != value) Marshal.WriteInt32(valAddr, value);
                        return;
                    }
                    idx = Marshal.ReadInt32(new IntPtr(nextPtr.ToInt64() + (long)idx * 4));
                }
            }
        }

        // Identifica el SCPrefab de la silla buscando el componente EPC_SCSeat
        // en Core._componentsMap (Dictionary<SCPrefab, EPC_SpaceshipComponent>).
        internal static void ResolveSeat(Core core)
        {
            if (SeatResolved || core == null) return;
            try
            {
                var map = core._componentsMap;
                if (map == null) return;
                foreach (var kv in map)
                {
                    var epc = kv.Value;
                    if (epc == null) continue;
                    if (epc.TryCast<EPC_SCSeat>() != null)
                    {
                        _seatPrefab = kv.Key.ToUlong();
                        SeatResolved = true;
                        ModLog.LogInfo($"[PlayerLimitMod] P4 — silla identificada: SCPrefab={_seatPrefab}");
                        return;
                    }
                }
            }
            catch (Exception ex) { ModLog.LogWarning($"[PlayerLimitMod] ResolveSeat: {ex.Message}"); }
        }

        private static void HookAvailableComponents()
        {
            try
            {
                var mod = Process.GetCurrentProcess().Modules
                    .Cast<ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName != null &&
                        m.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
                if (mod == null) { ModLog.LogError("[PlayerLimitMod] P4: GameAssembly.dll no encontrado."); return; }

                IntPtr addr = new IntPtr(mod.BaseAddress.ToInt64() + GET_AVAIL_RVA);
                _availDetour = INativeDetour.CreateAndApply(addr, (GetAvailDelegate)GetAvailHook, out _origGetAvail);
                ModLog.LogInfo("[PlayerLimitMod] P4 OK — GetAvailableComponents hookeado (silla → 8).");
            }
            catch (Exception ex) { ModLog.LogError($"[PlayerLimitMod] P4 excepción: {ex}"); }
        }

        // ── P3: memory-patch CMP EAX,4 → CMP EAX,MAX en CreateNetcoreClient ──
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(
            IntPtr lpAddress, UIntPtr dwSize,
            uint flNewProtect, out uint lpflOldProtect);

        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        private static void PatchServerSideLimit()
        {
            try
            {
                var mod = Process.GetCurrentProcess().Modules
                    .Cast<ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName != null &&
                        m.ModuleName.Equals("GameAssembly.dll",
                            StringComparison.OrdinalIgnoreCase));
                if (mod == null) { ModLog.LogError("[PlayerLimitMod] P3: GameAssembly.dll no encontrado."); return; }

                IntPtr patchAddr = new IntPtr(mod.BaseAddress.ToInt64() + SERVER_CHECK_RVA);
                byte current = Marshal.ReadByte(patchAddr);
                if (current != (byte)ORIGINAL_LIMIT)
                {
                    ModLog.LogWarning($"[PlayerLimitMod] P3 ABORTADO: esperaba 0x{ORIGINAL_LIMIT:X2}, encontré 0x{current:X2}.");
                    return;
                }
                if (!VirtualProtect(patchAddr, (UIntPtr)1, PAGE_EXECUTE_READWRITE, out uint old))
                {
                    ModLog.LogError($"[PlayerLimitMod] P3: VirtualProtect falló (win32={Marshal.GetLastWin32Error()}).");
                    return;
                }
                Marshal.WriteByte(patchAddr, (byte)MAX_PLAYERS);
                VirtualProtect(patchAddr, (UIntPtr)1, old, out _);
                ModLog.LogInfo($"[PlayerLimitMod] P3 OK — CreateNetcoreClient: CMP EAX,{ORIGINAL_LIMIT} → CMP EAX,{MAX_PLAYERS}");
            }
            catch (Exception ex) { ModLog.LogError($"[PlayerLimitMod] P3 excepción: {ex}"); }
        }

        // ── P4: boost directo a memoria nativa de array IL2CPP ─────────────
        // IL2CPP array (64-bit):  klass(8) + monitor(8) + bounds(8) + length(8) + data
        // Core.ComponentAmount:   _sc ptr(8) + _amount int(4) + pad(4) = stride 16
        private const int ARR_HEADER = 0x20;
        private const int CA_STRIDE  = 16;
        private const int CA_AMT_OFF = 8;

        // Tracks native addresses already boosted — prevents cascading ×2 across multiple Refresh calls.
        private static readonly System.Collections.Generic.HashSet<IntPtr> _boostedAddrs =
            new System.Collections.Generic.HashSet<IntPtr>();

        // setExact=true  → fija cada valor a MAX_PLAYERS (usado para los asientos en 'demo')
        // setExact=false → escala ×2 proporcional (resto de componentes)
        internal static bool BoostArrayField(IntPtr ownerPtr, long fieldOff, string label, bool setExact = false)
        {
            if (ownerPtr == IntPtr.Zero) return false;
            try
            {
                IntPtr arrayPtr = Marshal.ReadIntPtr(new IntPtr(ownerPtr.ToInt64() + fieldOff));
                if (arrayPtr == IntPtr.Zero) return false;
                int length = (int)Marshal.ReadInt64(new IntPtr(arrayPtr.ToInt64() + 0x18));
                if (length <= 0 || length > 256) return false;
                bool found = false;
                for (int i = 0; i < length; i++)
                {
                    IntPtr addr = new IntPtr(arrayPtr.ToInt64() + ARR_HEADER + (long)i * CA_STRIDE + CA_AMT_OFF);
                    if (_boostedAddrs.Contains(addr)) continue;
                    int old = Marshal.ReadInt32(addr);
                    if (old > 0 && old < MAX_PLAYERS)
                    {
                        int boosted = setExact
                            ? MAX_PLAYERS
                            : Math.Min(old * MAX_PLAYERS / ORIGINAL_LIMIT, MAX_PLAYERS);
                        Marshal.WriteInt32(addr, boosted);
                        _boostedAddrs.Add(addr);
                        ModLog.LogInfo($"[PlayerLimitMod] P4 {label}[{i}]: {old} → {boosted}");
                        found = true;
                    }
                }
                return found;
            }
            catch (Exception ex) { ModLog.LogWarning($"[PlayerLimitMod] P4 {label}: {ex.Message}"); return false; }
        }

        // ── P5: extiende _availablePlayerColors de 4 a MAX_PLAYERS ────────
        // Core._availablePlayerColors at field offset 0x58
        // Unity Color = float4 (r,g,b,a) → stride 16 bytes, stored inline
        // Allocamos un bloque con Marshal.AllocHGlobal (no-GC heap), copiamos
        // los colores originales y añadimos MAX_PLAYERS-4 colores extra.
        // Boehm GC no colecta memoria fuera del GC heap, así que el puntero es estable.
        private const long COLORS_FIELD_OFF = 0x58;
        private static bool _colorsExtended = false;

        // RGBA de los 4 colores extra (índices 4–7)
        // Paleta de colores extra (índices 4+). Soporta hasta MAX_PLAYERS grandes.
        private static readonly float[] ExtraColors = new float[]
        {
            1.0f, 0.50f, 0.08f, 1.0f,  // naranja
            0.65f, 0.15f, 1.0f,  1.0f,  // violeta
            0.08f, 0.88f, 0.88f, 1.0f,  // cian
            1.0f, 0.35f, 0.75f,  1.0f,  // rosa
            0.95f, 0.85f, 0.10f, 1.0f,  // amarillo
            0.20f, 0.80f, 0.30f, 1.0f,  // verde lima
            0.60f, 0.40f, 0.20f, 1.0f,  // marrón
            0.80f, 0.80f, 0.90f, 1.0f,  // blanco azulado
            0.50f, 0.50f, 0.50f, 1.0f,  // gris
            0.90f, 0.30f, 0.30f, 1.0f,  // rojo coral
            0.30f, 0.50f, 0.90f, 1.0f,  // azul cielo
            0.55f, 0.90f, 0.55f, 1.0f,  // verde menta
        };

        internal static unsafe void ExtendPlayerColors(IntPtr corePtr)
        {
            if (_colorsExtended) return;
            try
            {
                IntPtr existArr = Marshal.ReadIntPtr(new IntPtr(corePtr.ToInt64() + COLORS_FIELD_OFF));
                if (existArr == IntPtr.Zero) { ModLog.LogWarning("[PlayerLimitMod] P5: _availablePlayerColors es null."); return; }

                int existLen = (int)Marshal.ReadInt64(new IntPtr(existArr.ToInt64() + 0x18));
                if (existLen >= MAX_PLAYERS) { _colorsExtended = true; return; } // ya tiene suficientes

                IntPtr klassPtr = Marshal.ReadIntPtr(existArr); // klass ptr del array tipo Color[]

                // Nuevo bloque: header(32) + MAX_PLAYERS colores × 16 bytes cada uno
                int newSize = ARR_HEADER + MAX_PLAYERS * 16;
                IntPtr newArr = Marshal.AllocHGlobal(newSize);

                // Limpiar
                for (int i = 0; i < newSize; i++) Marshal.WriteByte(new IntPtr(newArr.ToInt64() + i), 0);

                // Escribir header IL2CPP
                Marshal.WriteIntPtr(newArr, klassPtr);
                Marshal.WriteInt64(new IntPtr(newArr.ToInt64() + 0x18), MAX_PLAYERS);

                // Copiar colores originales
                int copyBytes = existLen * 16;
                for (int i = 0; i < copyBytes; i++)
                {
                    byte b = Marshal.ReadByte(new IntPtr(existArr.ToInt64() + ARR_HEADER + i));
                    Marshal.WriteByte(new IntPtr(newArr.ToInt64() + ARR_HEADER + i), b);
                }

                // Escribir colores extra usando punteros float
                int extraCount = MAX_PLAYERS - existLen;
                for (int i = 0; i < extraCount && i < ExtraColors.Length / 4; i++)
                {
                    float* elem = (float*)(newArr.ToInt64() + ARR_HEADER + (existLen + i) * 16);
                    elem[0] = ExtraColors[i * 4];     // r
                    elem[1] = ExtraColors[i * 4 + 1]; // g
                    elem[2] = ExtraColors[i * 4 + 2]; // b
                    elem[3] = ExtraColors[i * 4 + 3]; // a
                }

                // Reemplazar puntero en Core
                Marshal.WriteIntPtr(new IntPtr(corePtr.ToInt64() + COLORS_FIELD_OFF), newArr);
                _colorsExtended = true;

                ModLog.LogInfo($"[PlayerLimitMod] P5 OK — _availablePlayerColors: {existLen} → {MAX_PLAYERS} colores.");
            }
            catch (Exception ex) { ModLog.LogWarning($"[PlayerLimitMod] P5 excepción: {ex.Message}"); }
        }
    }

    // ── P1: SteamMatchmaking.CreateLobby ──────────────────────────────────
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby))]
    internal static class Patch_CreateLobby
    {
        [HarmonyPrefix]
        static void Prefix(ref int cMaxMembers)
        {
            if (cMaxMembers <= Plugin.ORIGINAL_LIMIT)
            {
                Plugin.ModLog.LogInfo($"[PlayerLimitMod] P1 — CreateLobby: {cMaxMembers} → {Plugin.MAX_PLAYERS}");
                cMaxMembers = Plugin.MAX_PLAYERS;
            }
        }
    }

    // ── P2: SteamMatchmaking.SetLobbyMemberLimit ──────────────────────────
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.SetLobbyMemberLimit))]
    internal static class Patch_SetLobbyMemberLimit
    {
        [HarmonyPrefix]
        static void Prefix(ref int cMaxMembers)
        {
            if (cMaxMembers <= Plugin.ORIGINAL_LIMIT)
            {
                Plugin.ModLog.LogInfo($"[PlayerLimitMod] P2 — SetLobbyMemberLimit: {cMaxMembers} → {Plugin.MAX_PLAYERS}");
                cMaxMembers = Plugin.MAX_PLAYERS;
            }
        }
    }

    // ── P5 + resolver silla: Core.RefreshSharedAvailableComponents ────────
    // Prefix: extiende colores (P5). Postfix: identifica el SCPrefab de la silla
    // (necesario para que el hook nativo P4 sepa qué componente subir a 8).
    [HarmonyPatch(typeof(Core), "RefreshSharedAvailableComponents")]
    internal static class Patch_RefreshSharedAvailable
    {
        [HarmonyPrefix]
        static void Prefix(Core __instance)
        {
            if (!Plugin.EnablePlayerColors) return;
            try { Plugin.ExtendPlayerColors(__instance.Pointer); }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"[PlayerLimitMod] P5: {ex.Message}"); }
        }

        [HarmonyPostfix]
        static void Postfix(Core __instance)
        {
            if (!Plugin.SeatResolved) Plugin.ResolveSeat(__instance);
            Plugin.SeatDirty = true; // el juego recalculó el mapa → reaplicar la silla una vez
        }
    }

    // El mapa privado también se recalcula aquí → marcar para reaplicar la silla.
    [HarmonyPatch(typeof(Core), "RefreshPrivateAvailableComponents")]
    internal static class Patch_RefreshPrivateAvailable
    {
        [HarmonyPostfix]
        static void Postfix() => Plugin.SeatDirty = true;
    }


    // ── Overlay: muestra jugadores conectados en esquina superior-izquierda ──
    public class PlayerCounterOverlay : MonoBehaviour
    {
        public static CSteamID CurrentLobby = CSteamID.Nil;

        // Guarda el ID del lobby la primera vez que cualquier hook lo detecta.
        public static void Capture(CSteamID lobby)
        {
            if (!lobby.IsValid()) return;
            if (CurrentLobby != lobby)
            {
                CurrentLobby = lobby;
                Plugin.ModLog?.LogInfo($"[PlayerLimitMod] Lobby detectado: {lobby.m_SteamID}");
            }
        }

        // Lee Core.Get()._steam(0x288)._currentLobby(0x10) directo de memoria.
        // SteamManager guarda el lobby ACTUAL (se setea al crear o unirse).
        private static bool _loggedLobbyRead = false;
        static void TryReadLobbyFromGame()
        {
            try
            {
                var core = Core.Get();
                if (core == null) return;
                IntPtr steam = Marshal.ReadIntPtr(new IntPtr(core.Pointer.ToInt64() + 0x288)); // _steam
                if (steam == IntPtr.Zero) return;
                ulong id = (ulong)Marshal.ReadInt64(new IntPtr(steam.ToInt64() + 0x10));        // _currentLobby
                if (!_loggedLobbyRead)
                {
                    _loggedLobbyRead = true;
                    Plugin.ModLog?.LogInfo($"[PlayerLimitMod] Overlay — SteamManager._currentLobby = {id}");
                }
                if (id != 0) Capture(new CSteamID(id));
            }
            catch { }
        }

        void OnGUI()
        {
            string text;
            try
            {
                if (CurrentLobby == CSteamID.Nil || !CurrentLobby.IsValid())
                    TryReadLobbyFromGame();

                if (CurrentLobby == CSteamID.Nil || !CurrentLobby.IsValid())
                {
                    text = $"[Mod] Sin lobby  (max {Plugin.MAX_PLAYERS})";
                }
                else
                {
                    int count = SteamMatchmaking.GetNumLobbyMembers(CurrentLobby);
                    int limit = SteamMatchmaking.GetLobbyMemberLimit(CurrentLobby);
                    if (limit <= 0) limit = Plugin.MAX_PLAYERS;
                    if (count <= 0) count = 1;
                    text = $"[Mod] Jugadores: {count} / {limit}";
                }

                // Solo GUI.Label (DrawTexture/Box están stripped en este build IL2CPP).
                // Sombra negra desplazada + texto blanco para legibilidad sin fondo.
                GUI.color = Color.black;
                GUI.Label(new Rect(11, 9, 300, 24), text);
                GUI.color = Color.white;
                GUI.Label(new Rect(10, 8, 300, 24), text);
            }
            catch { /* nunca tumbar el render del juego */ }
        }
    }

    // Captura el ID del lobby al crearlo o unirse
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby))]
    internal static class Patch_LobbyCreated { } // lobby ID llega por callback, lo capturamos abajo

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.JoinLobby))]
    internal static class Patch_JoinLobby
    {
        [HarmonyPrefix]
        static void Prefix(CSteamID steamIDLobby)
        {
            PlayerCounterOverlay.Capture(steamIDLobby);
        }
    }

    // El host crea el lobby de forma asíncrona (callback LobbyCreated_t) y el
    // juego NO llama SetLobbyMemberLimit ni GetNumLobbyMembers por el wrapper.
    // Pero para dibujar la lista "Lobby (1) → wimow (You)" SÍ llama a
    // GetLobbyMemberByIndex / GetLobbyData / GetLobbyOwner. Capturamos el ID
    // desde cualquiera de esas: la primera que dispare nos da el lobby real.
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.GetNumLobbyMembers))]
    internal static class Patch_GetNumLobbyMembers_Capture
    {
        [HarmonyPrefix] static void Prefix(CSteamID steamIDLobby) => PlayerCounterOverlay.Capture(steamIDLobby);
    }

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.GetLobbyMemberLimit))]
    internal static class Patch_GetLobbyMemberLimit_Capture
    {
        [HarmonyPrefix] static void Prefix(CSteamID steamIDLobby) => PlayerCounterOverlay.Capture(steamIDLobby);
    }

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.GetLobbyMemberByIndex))]
    internal static class Patch_GetLobbyMemberByIndex_Capture
    {
        [HarmonyPrefix] static void Prefix(CSteamID steamIDLobby) => PlayerCounterOverlay.Capture(steamIDLobby);
    }

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.GetLobbyData))]
    internal static class Patch_GetLobbyData_Capture
    {
        [HarmonyPrefix] static void Prefix(CSteamID steamIDLobby) => PlayerCounterOverlay.Capture(steamIDLobby);
    }

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.SetLobbyData))]
    internal static class Patch_SetLobbyData_Capture
    {
        [HarmonyPrefix] static void Prefix(CSteamID steamIDLobby) => PlayerCounterOverlay.Capture(steamIDLobby);
    }

    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.GetLobbyOwner))]
    internal static class Patch_GetLobbyOwner_Capture
    {
        [HarmonyPrefix] static void Prefix(CSteamID steamIDLobby) => PlayerCounterOverlay.Capture(steamIDLobby);
    }

    // Crea el GameObject con el overlay en la primera escena disponible
    [HarmonyPatch(typeof(Core), "RefreshSharedAvailableComponents")]
    internal static class Patch_SpawnOverlay
    {
        private static bool _spawned = false;
        [HarmonyPostfix]
        static void Postfix()
        {
            if (!Plugin.EnableOverlay || _spawned) return;
            _spawned = true;
            try
            {
                var go = new GameObject("PlayerLimitMod_Overlay");
                go.AddComponent<PlayerCounterOverlay>();
                UnityEngine.Object.DontDestroyOnLoad(go);
                Plugin.ModLog.LogInfo("[PlayerLimitMod] Overlay de jugadores creado.");
            }
            catch (Exception ex)
            {
                // El overlay es opcional; nunca debe tumbar el resto del mod.
                Plugin.ModLog.LogWarning($"[PlayerLimitMod] Overlay no creado: {ex.Message}");
            }
        }
    }

    internal static class PluginInfo
    {
        public const string GUID    = "com.mods.approxup.playerlimit";
        public const string Name    = "PlayerLimitMod";
        public const string Version = "1.0.19";
    }
}
