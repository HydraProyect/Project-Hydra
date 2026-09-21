using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Nivel 0 (DEC-33, REC-035): toda clase de Application que reciba por constructor un
/// puerto de IA tiene que recibir también <c>IInstruccionTratamientoIaService</c>, el
/// único punto de consulta de la instrucción de tratamiento del Tenant propietario.
///
/// Es exactamente el descuido que este ratchet convierte en build rojo: las tres
/// detecciones de la ingesta de correo (relevancia CAE, sugerencia de visita, sugerencia
/// de gestión) y la rama PdfVisual de la detección de campos de plantilla se escribieron
/// copiando al vecino, llamaron al proveedor durante meses sin consultar nada, y nada
/// falló ni avisó — el cuerpo del correo, y con él nombres y DNI de Trabajadores, salía
/// igual. Mientras REC-104 (gateway común de IA) no exista, la comprobación se repite en
/// cada consumidor, y una lista escrita a mano es lo único que distingue "no le hace
/// falta" de "se olvidó".
///
/// Mismo mecanismo de ratchet por texto que <see cref="ClientesDeIaConResilienciaPropiaTests"/>:
/// el gate es una línea de código, no un tipo en la firma, así que un test de tipos no
/// podría verlo; lo que sí se puede exigir es la dependencia que lo hace posible.
/// </summary>
public class ConsumidoresDeIaConsultanLaInstruccionDeTratamientoTests
{
    private const string DirectorioApplication = "src/CaeManager.Application";

    private const string ServicioDeInstruccion = "IInstruccionTratamientoIaService";

    /// <summary>
    /// Puertos de IA de Application: cada uno lo implementa en Infrastructure un cliente
    /// de un proveedor de pago (ver <see cref="ClientesDeIaConResilienciaPropiaTests"/>),
    /// salvo el router, que es el despachador común a los tres proveedores documentales.
    ///
    /// Los dos últimos no son puertos «de alto nivel» sino el acceso crudo a los
    /// proveedores documentales, y estaban fuera de esta lista hasta que la revisión de
    /// Codex lo señaló: una clase nueva que se saltara el router y pidiera
    /// <c>IDocumentAIProviderFactory</c> llegaba a los mismos proveedores de pago sin que
    /// el detector la viera. Al añadirlos, el conjunto detectado pasó de 10 a 11 ficheros
    /// —comprobado antes de tocar la lista, porque un detector ampliado que no encuentra
    /// nada nuevo suele significar que la ampliación no funciona—.
    /// </summary>
    private static readonly string[] PuertosDeIa =
    [
        "IAsistenteIaService",
        "IDocumentAIRouterService",
        "IExtraccionMetadatosDocumentoIaService",
        "IExtraccionTrabajadoresIaService",
        "IDeteccionVisitaCorreoService",
        "IDeteccionGestionCorreoService",
        "IDeteccionRelevanciaCaeService",
        "IDocumentAIProviderFactory",
        "IDocumentAIProvider",
    ];

    /// <summary>
    /// Únicas exenciones, cada una con su motivo. NO es la lista de consumidores
    /// conocidos: esa se comprueba aparte, en
    /// <see cref="El_detector_sigue_encontrando_a_los_consumidores_conocidos"/>, para que
    /// una lista con dos papeles no acabe tapando lo que debería vigilar.
    /// </summary>
    private static readonly Dictionary<string, string> ExentosConMotivo = new()
    {
        ["DocumentosIa/DocumentAIRouterService.cs"] =
            "despachador común a los tres proveedores documentales: no tiene decisión de cumplimiento " +
            "propia, y sus cuatro llamadores de Application los vigila este mismo ratchet, porque " +
            "IDocumentAIRouterService está en PuertosDeIa — quien quiera usarlo tiene que recibir el " +
            "servicio de instrucción. Cuando exista REC-104 (gateway común de IA), DEC-46 fija que la " +
            "consulta se hace una sola vez en su punto de entrada y esta exención desaparece",

        ["DocumentosIa/RouterExtraccionMetadatosDocumentoIaService.cs"] =
            "adaptador de un puerto a otro: no decide nada ni tiene tenant a mano, y su único " +
            "consumidor (VerificacionIaDocumentoService) ya consulta el Nivel 0 antes de llamarlo",
    };

    /// <summary>
    /// Los consumidores que el detector tiene que seguir viendo. Si un renombrado o un
    /// cambio de sitio deja alguno fuera, el ratchet se habría vuelto ciego sin ponerse
    /// rojo — que es justo el fallo que vigila.
    /// </summary>
    private static readonly string[] ConsumidoresConocidos =
    [
        "AsistenteIa/Queries/PreguntarAlAsistente/PreguntarAlAsistenteQuery.cs",
        "Comunicaciones/Queries/DetectarActualizacionDocumentoDesdeAdjunto/DetectarActualizacionDocumentoDesdeAdjuntoQuery.cs",
        "Comunicaciones/Deteccion/IRelevanciaCaeService.cs",
        "Comunicaciones/Deteccion/ISugerenciaVisitaCorreoService.cs",
        "Comunicaciones/Deteccion/SugerenciaGestionCorreoService.cs",
        "Documentos/Queries/DetectarCamposDocumento/DetectarCamposDocumentoQuery.cs",
        "Documentos/Verificacion/VerificacionIaDocumentoService.cs",
        "Plantillas/Queries/DetectarCamposPlantilla/DetectarCamposPlantillaQueryHandler.cs",
        "Trabajadores/Deteccion/DeteccionTrabajadoresService.cs",
    ];

    [Fact]
    public void Todo_consumidor_de_un_puerto_de_ia_recibe_el_servicio_de_instruccion_de_tratamiento()
    {
        var infractores = ArchivosQueInyectanUnPuertoDeIa()
            .Where(archivo => !ExentosConMotivo.ContainsKey(archivo.Ruta))
            .Where(archivo => !archivo.Contenido.Contains(ServicioDeInstruccion, StringComparison.Ordinal))
            .Select(archivo => archivo.Ruta)
            .ToList();

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            $"estas clases mandan datos del Tenant propietario a un proveedor de IA sin recibir {ServicioDeInstruccion}, " +
            "así que no pueden estar comprobando el Nivel 0 (DEC-33, REC-035). Inyéctalo y corta antes de llamar al " +
            "proveedor, como hacen VerificacionIaDocumentoService y el resto; si de verdad no aplica, añádelo a " +
            "ExentosConMotivo explicando por qué");
    }

    [Fact]
    public void El_detector_sigue_encontrando_a_los_consumidores_conocidos()
    {
        var detectados = ArchivosQueInyectanUnPuertoDeIa().Select(a => a.Ruta).ToHashSet(StringComparer.Ordinal);

        var invisibles = ConsumidoresConocidos.Where(c => !detectados.Contains(c)).ToList();

        string.Join(Environment.NewLine, invisibles).Should().BeEmpty(
            "el detector ya no ve a estos consumidores de IA: o cambiaron de ruta o cambió la forma de inyectar el " +
            "puerto, y en cualquiera de los dos casos el ratchet estaría dando verde por no mirar a nadie. " +
            "Reapúntalo en el mismo commit");
    }

    [Fact]
    public void Cada_exento_sigue_existiendo_y_sigue_siendo_consumidor()
    {
        var detectados = ArchivosQueInyectanUnPuertoDeIa().Select(a => a.Ruta).ToHashSet(StringComparer.Ordinal);

        var sobrantes = ExentosConMotivo.Keys.Where(e => !detectados.Contains(e)).ToList();

        string.Join(Environment.NewLine, sobrantes).Should().BeEmpty(
            "estas exenciones ya no corresponden a ningún consumidor de un puerto de IA: una exención que sobrevive " +
            "a su motivo es un agujero esperando a que alguien reutilice el nombre. Bórrala");
    }

    private static IEnumerable<(string Ruta, string Contenido)> ArchivosQueInyectanUnPuertoDeIa()
    {
        var raizApplication = Path.Combine(RaizDelRepositorio(), DirectorioApplication.Replace('/', Path.DirectorySeparatorChar));

        Directory.Exists(raizApplication).Should().BeTrue(
            "si Application cambia de sitio, este ratchet deja de vigilar nada y hay que reapuntarlo");

        // Parámetro de constructor: abierto por "(" o por la coma del parámetro anterior,
        // el puerto, su nombre, y el cierre. Exigir los dos extremos deja fuera el `using`,
        // la declaración de la propia interfaz y —lo que más ruido daba— las menciones en
        // prosa dentro de un comentario; \b evita además que un puerto case por prefijo
        // con otro más largo.
        var patron = new Regex(
            $@"[(,]\s*\b({string.Join('|', PuertosDeIa)})\b\s+[a-z_]\w*\s*[,)]", RegexOptions.Compiled);

        foreach (var archivo in Directory.EnumerateFiles(raizApplication, "*.cs", SearchOption.AllDirectories))
        {
            if (archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var contenido = File.ReadAllText(archivo);
            if (!patron.IsMatch(contenido))
                continue;

            var ruta = Path.GetRelativePath(raizApplication, archivo).Replace(Path.DirectorySeparatorChar, '/');
            yield return (ruta, contenido);
        }
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
