# Approximately Up — Multiplayer Mod (8 jugadores)

Mod que aumenta el límite de jugadores de **4 a 8** en *Approximately Up (Demo)*.

> **El código fuente está en [`src/Plugin.cs`](src/Plugin.cs)** — podés leerlo completo antes de instalar cualquier cosa.

---

## ¿Qué hace?

| # | Qué modifica | Por qué |
|---|---|---|
| P1 | Tamaño del lobby de Steam | El juego lo crea con límite 4; lo sube a 8 |
| P2 | `SetLobbyMemberLimit` de Steam | Refuerza el límite al crear el lobby |
| P3 | Un byte en `GameAssembly.dll` (en memoria, no en disco) | El servidor interno del juego rechazaba conexiones >4 |
| P4 | Inventario de componentes (asientos, etc.) | El garage solo dejaba colocar 4 asientos |
| P5 | Array de colores de jugadores | Agrega 4 colores extra para que cada jugador tenga el suyo |

El mod usa **[BepInEx](https://github.com/BepInEx/BepInEx)**, un framework de modding open-source con cientos de miles de usuarios en juegos Unity. No modifica ningún archivo del juego — todo se aplica en memoria al iniciar.

---

## Instalación (2 pasos)

### Requisito
Tener *Approximately Up Demo* instalado en Steam.

### Paso 1 — Encontrar la carpeta del juego
En Steam: click derecho en el juego → *Administrar* → *Ver archivos locales*.

La ruta suele ser:
```
C:\Program Files (x86)\Steam\steamapps\common\Approximately Up Demo\
```

### Paso 2 — Copiar los archivos
Copiá el **contenido** de esta carpeta (los archivos `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` y la carpeta `BepInEx/`) dentro de la carpeta del juego.

Si Windows pregunta si querés combinar carpetas → **Sí**.

```
Approximately Up Demo/
├── winhttp.dll          ← copiar acá
├── doorstop_config.ini  ← copiar acá
├── .doorstop_version    ← copiar acá
└── BepInEx/
    ├── core/            ← copiar acá
    ├── unity-libs/      ← copiar acá
    └── plugins/
        └── PlayerLimitMod.dll  ← el mod
```

### Listo
Abrí el juego normalmente desde Steam. En el **primer inicio** BepInEx puede tardar ~1 minuto extra generando archivos internos — es normal.

---

## Verificar que funciona

Abrí `BepInEx/LogOutput.log` dentro de la carpeta del juego y buscá estas líneas:

```
[Info :PlayerLimitMod] [PlayerLimitMod] Harmony patches aplicados.
[Info :PlayerLimitMod] [PlayerLimitMod] P3 OK — CreateNetcoreClient: CMP EAX,4 → CMP EAX,8
[Info :PlayerLimitMod] [PlayerLimitMod] P5 OK — _availablePlayerColors: 4 → 8 colores.
```

Si aparecen, el mod está activo.

---

## Desinstalar

Borrá o mové `BepInEx/plugins/PlayerLimitMod.dll`. El juego vuelve a funcionar normal.

Para desinstalar BepInEx completamente: borrá `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` y la carpeta `BepInEx/`.

---

## Preguntas frecuentes

**¿Modifica archivos del juego?**
No. Todo se aplica en memoria al iniciar. Si borrás el mod, el juego queda exactamente igual que antes.

**¿Funciona en la versión de Steam normal?**
Sí, siempre que sea *Approximately Up Demo*.

**¿Necesito que todos mis amigos lo instalen?**
Sí, todos los jugadores tienen que tenerlo instalado.

**¿Tiene virus?**
El código fuente está en `src/Plugin.cs` — podés leerlo. Si sabés C# podés compilarlo vos mismo con `dotnet build` y usar tu propio `.dll`.
