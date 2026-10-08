using System.Text.Json;
using Microsoft.Playwright;

namespace CaeManager.E2ETests.Fidelidad360;

/// <summary>
/// Una magnitud medida en un lado (ficha o mockup): los valores distintos que toma una
/// propiedad calculada en todas las apariciones de una pieza, de más a menos frecuente y
/// unidos por « | ». Un lado bien construido da un único valor por magnitud; varios valores
/// ya son un hallazgo («hay pastillas de dos tamaños»), y por eso no se promedian.
/// </summary>
public sealed record Magnitud(string Tipo, string Valor, int Apariciones);

/// <summary>Lo que el medidor leyó de un lado, por clave «pieza.propiedad».</summary>
public sealed record Medicion(string Lado, IReadOnlyDictionary<string, Magnitud> Magnitudes, IReadOnlyDictionary<string, int> PiezasVistas);

/// <summary>
/// Selector de cada pieza en un lado. Por convención una pieza se localiza por
/// <c>[data-pieza="…"]</c>, que es lo que emiten los componentes compartidos del producto
/// (Badge, Tarjeta, CuerpoConLateral, AnilloCumplimiento, FilaRelacion, CabeceraPagina) y lo
/// que debe emitir un mockup nuevo. Un mockup anterior a la convención declara aquí sus
/// propios selectores; nunca se «prueban los dos», para que un selector que deja de casar
/// se vea como pieza ausente y no como otra pieza distinta que casualmente casa.
/// </summary>
public sealed record SelectoresDeLado(string Raiz, IReadOnlyDictionary<string, string> Piezas)
{
    public const string Pastilla = "pastilla";
    public const string Tarjeta = "tarjeta";
    public const string Lateral = "lateral";
    public const string Anillo = "anillo";
    public const string Cabecera = "cabecera-identidad";
    public const string Fila = "fila";
    public const string FilaDetalle = "fila-detalle";
    public const string FilaProblemaPeligro = "fila-problema-peligro";
    public const string FilaProblemaAdvertencia = "fila-problema-advertencia";

    public static readonly IReadOnlyDictionary<string, string> PorConvencion = new Dictionary<string, string>
    {
        [Pastilla] = "[data-pieza=\"pastilla\"]",
        [Tarjeta] = "[data-pieza=\"tarjeta\"]",
        [Lateral] = "[data-pieza=\"lateral\"]",
        [Anillo] = "[data-pieza=\"anillo\"]",
        [Cabecera] = "[data-pieza=\"cabecera-identidad\"]",
        [Fila] = "[data-pieza=\"fila\"]",
        [FilaDetalle] = "[data-pieza=\"fila-detalle\"]",
        [FilaProblemaPeligro] = "[data-pieza=\"fila\"][data-tono=\"peligro\"]",
        [FilaProblemaAdvertencia] = "[data-pieza=\"fila\"][data-tono=\"advertencia\"]",
    };

    public static SelectoresDeLado Convencion(string raiz = "body") => new(raiz, PorConvencion);

    /// <summary>La convención, con los selectores propios de un mockup que no la sigue.</summary>
    public static SelectoresDeLado Con(string raiz, IReadOnlyDictionary<string, string> propios)
    {
        var piezas = new Dictionary<string, string>(PorConvencion);
        foreach (var (pieza, selector) in propios)
        {
            if (!piezas.ContainsKey(pieza))
                throw new ArgumentException($"«{pieza}» no es una pieza del catálogo.", nameof(propios));
            piezas[pieza] = selector;
        }

        return new SelectoresDeLado(raiz, piezas);
    }
}

/// <summary>
/// Lee estilos calculados de las piezas clave de una ficha 360 en la página que se le pasa.
/// Es el mismo guion para la ficha Blazor y para el mockup: lo único que cambia entre lados
/// son los selectores. No navega, no cambia de tema y no espera: quien llama deja la página
/// en el estado que quiere medir.
/// </summary>
public static class MedidorPiezas360
{
    public static async Task<Medicion> MedirAsync(IPage page, string lado, SelectoresDeLado selectores)
    {
        // El guion devuelve texto JSON: el serializador de Playwright no conserva tal cual un
        // objeto cuyas claves son arbitrarias («pastilla[Vencido].color»).
        var texto = await page.EvaluateAsync<string>(Guion, new { raiz = selectores.Raiz, piezas = selectores.Piezas });
        using var documento = JsonDocument.Parse(texto);
        var bruto = documento.RootElement;

        if (bruto.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"No se pudo medir «{lado}»: {error.GetString()}");

        var magnitudes = new SortedDictionary<string, Magnitud>(StringComparer.Ordinal);
        foreach (var propiedad in bruto.GetProperty("magnitudes").EnumerateObject())
        {
            magnitudes[propiedad.Name] = new Magnitud(
                propiedad.Value.GetProperty("tipo").GetString()!,
                propiedad.Value.GetProperty("valor").GetString()!,
                propiedad.Value.GetProperty("n").GetInt32());
        }

        var vistas = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var pieza in bruto.GetProperty("vistas").EnumerateObject())
            vistas[pieza.Name] = pieza.Value.GetInt32();

        return new Medicion(lado, magnitudes, vistas);
    }

    // Tipos de magnitud: «px» (longitud o número, se compara con tolerancia), «color»
    // (#rrggbbaa, tolerancia por canal) y «texto» (igualdad exacta).
    private const string Guion = """
        ({ raiz, piezas }) => {
          const R = document.querySelector(raiz);
          if (!R) return JSON.stringify({ error: `la raíz «${raiz}» no existe en la página` });

          const visible = el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
          const todos = pieza => [...R.querySelectorAll(piezas[pieza])].filter(visible);

          const hex = c => '#' + c.map(x => Math.max(0, Math.min(255, Math.round(x))).toString(16).padStart(2, '0')).join('');
          const color = v => {
            let m = v.match(/^rgba?\(([^)]+)\)$/);
            if (m) { const p = m[1].split(/[,\s\/]+/).filter(Boolean).map(Number); return hex([p[0], p[1], p[2], p.length > 3 ? p[3] * 255 : 255]); }
            m = v.match(/^color\(srgb ([^)]+)\)$/);
            if (m) { const p = m[1].split(/[\s\/]+/).filter(Boolean).map(Number); return hex([p[0] * 255, p[1] * 255, p[2] * 255, p.length > 3 ? p[3] * 255 : 255]); }
            return 'ilegible:' + v;
          };
          const px = v => { const n = parseFloat(v); return Number.isFinite(n) ? String(Math.round(n * 100) / 100) : v; };
          const familia = v => v.split(',')[0].trim().replace(/["']/g, '').toLowerCase();

          const magnitudes = {};
          const pon = (clave, tipo, valores) => {
            if (!valores.length) return;
            const cuenta = new Map();
            for (const v of valores) cuenta.set(v, (cuenta.get(v) || 0) + 1);
            const distintos = [...cuenta.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1)).map(e => e[0]);
            magnitudes[clave] = { tipo, valor: distintos.join(' | '), n: valores.length };
          };
          const estilo = (clave, tipo, elementos, propiedad, leer) =>
            pon(clave, tipo, elementos.map(el => (leer || (x => x))(getComputedStyle(el).getPropertyValue(propiedad))));

          const fondoEfectivo = el => {
            for (let e = el; e; e = e.parentElement) {
              const c = color(getComputedStyle(e).backgroundColor);
              if (!c.endsWith('00')) return c;
            }
            return '#ffffffff';
          };

          const pastillas = todos('pastilla'), tarjetas = todos('tarjeta'), laterales = todos('lateral'),
                anillos = todos('anillo'), cabeceras = todos('cabecera-identidad'), detalles = todos('fila-detalle'),
                filasPeligro = todos('fila-problema-peligro'), filasAdvertencia = todos('fila-problema-advertencia');
          const conProblema = new Set([...filasPeligro, ...filasAdvertencia]);
          const filas = todos('fila');

          // Fondo de página: el primer fondo opaco por detrás del lateral (o de la raíz).
          pon('fondo-pagina.background-color', 'color', [fondoEfectivo(laterales[0] ? laterales[0].parentElement : R)]);

          estilo('tarjeta.background-color', 'color', tarjetas, 'background-color', color);
          estilo('tarjeta.border-top-color', 'color', tarjetas, 'border-top-color', color);
          estilo('tarjeta.border-top-width', 'px', tarjetas, 'border-top-width', px);
          estilo('tarjeta.border-top-left-radius', 'px', tarjetas, 'border-top-left-radius', px);
          estilo('tarjeta.box-shadow', 'texto', tarjetas, 'box-shadow');

          const selTarjeta = piezas['tarjeta'];
          pon('cabecera-identidad.en-tarjeta', 'texto', cabeceras.map(c => c.closest(selTarjeta) ? 'sí' : 'no'));

          pon('anillo.width', 'px', anillos.map(a => px(a.getBoundingClientRect().width)));
          pon('anillo.height', 'px', anillos.map(a => px(a.getBoundingClientRect().height)));
          pon('lateral.width', 'px', laterales.map(l => px(l.getBoundingClientRect().width)));

          // La pastilla de estado de una fila es la última pastilla de esa fila.
          const selFila = piezas['fila'];
          const deFila = new Map();
          for (const p of pastillas) { const f = p.closest(selFila); if (f && R.contains(f)) deFila.set(f, p); }
          const estado = [...deFila.values()];
          const sueltas = pastillas.filter(p => !p.closest(selFila));

          const tipografia = (prefijo, elementos) => {
            estilo(prefijo + '.font-family', 'texto', elementos, 'font-family', familia);
            estilo(prefijo + '.font-weight', 'px', elementos, 'font-weight', px);
            estilo(prefijo + '.font-size', 'px', elementos, 'font-size', px);
            estilo(prefijo + '.line-height', 'px', elementos, 'line-height', px);
            estilo(prefijo + '.border-top-width', 'px', elementos, 'border-top-width', px);
          };
          tipografia('pastilla', sueltas);
          tipografia('pastilla-de-fila', estado);

          // Colores por rótulo: la misma pastilla «Vencido» se compara entre lados aunque
          // los datos de ejemplo difieran. Las cifras se normalizan («3 vencidos» → «# vencidos»).
          const porRotulo = new Map();
          for (const p of pastillas) {
            const rotulo = p.textContent.trim().replace(/\s+/g, ' ').replace(/\d+/g, '#');
            if (!rotulo) continue;
            if (!porRotulo.has(rotulo)) porRotulo.set(rotulo, []);
            porRotulo.get(rotulo).push(p);
          }
          for (const [rotulo, elementos] of porRotulo) {
            estilo(`pastilla[${rotulo}].color`, 'color', elementos, 'color', color);
            estilo(`pastilla[${rotulo}].background-color`, 'color', elementos, 'background-color', color);
            estilo(`pastilla[${rotulo}].border-top-color`, 'color', elementos, 'border-top-color', color);
          }

          // Columna fija: dentro de una misma lista, el borde izquierdo de la pastilla de
          // estado cae en la misma vertical en todas las filas.
          const porLista = new Map();
          for (const [f, p] of deFila) {
            if (!porLista.has(f.parentElement)) porLista.set(f.parentElement, []);
            porLista.get(f.parentElement).push({ izq: p.getBoundingClientRect().left, borde: f.getBoundingClientRect().right });
          }
          const dispersiones = [], distancias = [];
          for (const grupo of porLista.values()) {
            for (const g of grupo) distancias.push(px(g.borde - g.izq));
            if (grupo.length < 2) continue;
            const xs = grupo.map(g => g.izq);
            dispersiones.push(Math.max(...xs) - Math.min(...xs));
          }
          if (dispersiones.length) pon('pastilla-de-fila.dispersion-izquierda', 'px', [px(Math.max(...dispersiones))]);
          pon('pastilla-de-fila.distancia-al-borde-derecho', 'px', distancias);

          // Divisor: borde superior de las filas que no abren lista ni llevan problema.
          const conDivisor = filas.filter(f => !conProblema.has(f) && f.previousElementSibling && f.previousElementSibling.matches(selFila));
          estilo('fila.border-top-color', 'color', conDivisor, 'border-top-color', color);
          estilo('fila.border-top-width', 'px', conDivisor, 'border-top-width', px);
          estilo('fila-detalle.color', 'color', detalles, 'color', color);

          const problema = (prefijo, elementos) => {
            estilo(prefijo + '.background-color', 'color', elementos, 'background-color', color);
            estilo(prefijo + '.degradado', 'texto', elementos, 'background-image', v => v.includes('gradient') ? 'sí' : 'no');
          };
          problema('fila-con-problema[peligro]', filasPeligro);
          problema('fila-con-problema[advertencia]', filasAdvertencia);

          const vistas = {
            pastilla: pastillas.length, 'pastilla-de-fila': estado.length, tarjeta: tarjetas.length, lateral: laterales.length,
            anillo: anillos.length, 'cabecera-identidad': cabeceras.length, fila: filas.length, 'fila-detalle': detalles.length,
            'fila-problema-peligro': filasPeligro.length, 'fila-problema-advertencia': filasAdvertencia.length,
          };
          return JSON.stringify({ magnitudes, vistas });
        }
        """;
}
