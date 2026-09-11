# Portal de prueba para la extensión de navegador

Fixture estático (sin backend) con las cuatro formas de widget de subida de
archivo que puede tener una plataforma CAE externa real — ver
`ARQUITECTURA-INTEGRACIONES.md` § 14.5 (repositorio de negocio): un test verde
con Playwright/`setInputFiles` no demuestra que el truco `DataTransfer` de la
extensión (`extension/content.js`) funcione contra un portal real, porque
Playwright inyecta el archivo por CDP, no por el mecanismo que usa la
extensión. Este portal existe para probar el mecanismo real, con un gesto de
usuario real, antes de dar cualquier conector por funcional.

## Las cuatro páginas

| Página | Qué simula | Qué prueba |
|---|---|---|
| `input-simple.html` | `<input type="file">` sin más | Caso base — ya verificado a mano el 2026-09-07 |
| `input-controlado-react.html` | Un input de React que controla su valor por estado (patrón habitual en portales modernos) | Si React observa el evento `change` sintético que dispara `inyectarEnInput()` |
| `dropzone-sin-input.html` | Una zona de arrastrar-y-soltar sin ningún `<input type="file">` en el DOM (patrón `react-dropzone` y similares) | Si el mecanismo actual (que solo sabe rellenar un `input[type=file]`) falla limpio o falla mal cuando no hay ningún input que rellenar |
| `pagina-exterior-iframe.html` (embebe `pagina-interior-iframe.html` en un origen distinto) | Un widget de subida de un tercero embebido en `<iframe>` de otro origen | Si el content script llega o no al DOM de un iframe, de otro origen o no, con la configuración real de `manifest.json` (`all_frames` no declarado, es decir `false`) |

## Cómo servirlo

Este portal no puede abrirse con `file://` porque los content scripts de la
extensión no se inyectan ahí salvo declaración expresa (que la extensión real
no tiene, y este fixture tampoco pide). Sirve las cuatro páginas con
cualquier servidor estático, por ejemplo:

```bash
cd tools/PortalPruebaExtension
python -m http.server 8766
```

Para el caso del iframe cross-origin, abre la página exterior por
`http://localhost:8766/pagina-exterior-iframe.html` — el iframe interior
apunta a `http://127.0.0.1:8766/...`, que el navegador trata como un origen
distinto de `localhost` aunque sea el mismo servidor y puerto.

## Cómo probar con la extensión real

`manifest.json` no declara estos hosts en `content_scripts.matches` (y no
debería — añadir un puerto local fijo a la extensión que se publica sería
ampliar su superficie de host permanentemente por un fixture de desarrollo).
Para probar, usa una copia de trabajo fuera del repositorio con
`http://localhost:8766/*` y `http://127.0.0.1:8766/*` añadidos a
`content_scripts.matches`, cárgala como *unpacked extension* en
`chrome://extensions`, y repite para cada página. El resultado de cada caso
se registra en `ARQUITECTURA-INTEGRACIONES.md` (repositorio de negocio), no
aquí — este directorio es solo la herramienta, no el informe.
