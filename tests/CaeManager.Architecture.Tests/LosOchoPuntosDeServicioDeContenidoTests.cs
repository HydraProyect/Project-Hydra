using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// HO-099-01 § 6 (REC-099): los ocho puntos que sirven bytes de
/// <c>IFileStorageService.AbrirAsync</c> a un navegador, medidos por la
/// Oficina de Reconciliación. Tres tienen debajo contenido clasificable por
/// <c>TipoDocumento.Sensibilidad</c> — dos instancias reales de
/// <see cref="CaeManager.Domain.Documentos.Documento"/>, y la evidencia de
/// <c>VerificacionExternaSubcontrata</c> (Codex, HO-099-01: tiene su propio
/// <c>TipoDocumentoId</c>, la exclusión original asumía lo contrario) — este
/// ratchet deja esa medición vigilada por texto, para que ninguno de los
/// ocho cambie de comportamiento en silencio: ni un punto que debería
/// registrar deja de hacerlo, ni uno que no debe registrar empieza a llamar
/// al servicio sin que nadie revise por qué (que sería exactamente
/// "registrar de más", DEC-36).
///
/// <para>
/// ⚠️ Ratchet de texto por archivo, mismo criterio que
/// <see cref="SensibilidadDocumentalUnPuntoDeConsultaTests"/>: prueba que la
/// llamada (o su ausencia deliberada, con el comentario que la explica) sigue
/// en el fichero — no ejecuta el endpoint ni prueba el comportamiento en
/// runtime, eso lo cubre <c>RegistroAccesoDocumentoSensibleServiceTests</c>
/// (Integration, contra Postgres real).
/// </para>
/// </summary>
public class LosOchoPuntosDeServicioDeContenidoTests
{
    private const string LlamadaAlServicio = "RegistrarSiSensibleAsync";
    private const string ComentarioDeExclusion = "No pasa por IRegistroAccesoDocumentoSensibleService";

    public static TheoryData<string> PuntosQueDebenRegistrar => new()
    {
        "src/CaeManager.Web/Features/Documentos/DocumentosEndpoints.cs",
        "src/CaeManager.Web/Features/Auditoria/AuditoriaEndpoints.cs",
        "src/CaeManager.Web/Features/Subcontratas/SubcontratasEndpoints.cs"
    };

    public static TheoryData<string> PuntosQueNoDebenRegistrar => new()
    {
        "src/CaeManager.Web/Features/Comunicaciones/ComunicacionesEndpoints.cs",
        "src/CaeManager.Web/Features/Centros/RequisitosDocumentalesEndpoints.cs",
        "src/CaeManager.Web/Features/Documentos/FirmasGuardadasEndpoints.cs",
        "src/CaeManager.Web/Features/Plantillas/Pages/ConfigurarPlantilla.razor.cs"
    };

    [Theory]
    [MemberData(nameof(PuntosQueDebenRegistrar))]
    public void Los_tres_puntos_con_contenido_clasificable_llaman_al_servicio(string rutaRelativa)
    {
        var contenido = LeerArchivo(rutaRelativa);
        contenido.Should().Contain(LlamadaAlServicio,
            $"{rutaRelativa} sirve contenido clasificable por TipoDocumento.Sensibilidad");
    }

    [Theory]
    [MemberData(nameof(PuntosQueNoDebenRegistrar))]
    public void Los_puntos_sin_contenido_clasificable_no_llaman_al_servicio_y_dejan_dicho_por_que(string rutaRelativa)
    {
        var contenido = LeerArchivo(rutaRelativa);

        contenido.Should().NotContain(LlamadaAlServicio,
            $"{rutaRelativa} no sirve contenido clasificable — llamar aquí sería registrar de más (DEC-36)");
        contenido.Should().Contain(ComentarioDeExclusion,
            $"{rutaRelativa} debe dejar explícito por qué no registra, para que no se lea como un olvido");
    }

    /// <summary>
    /// FirmasGuardadasEndpoints.cs sirve DOS puntos (firma y sello) — el test
    /// de arriba ya comprueba que el archivo entero lleva el comentario al
    /// menos una vez; este control adicional exige que aparezca en las DOS
    /// llamadas a AbrirAsync del archivo, no solo en la primera.
    /// </summary>
    [Fact]
    public void FirmasGuardadasEndpoints_explica_los_dos_puntos_que_sirve()
    {
        var contenido = LeerArchivo("src/CaeManager.Web/Features/Documentos/FirmasGuardadasEndpoints.cs");

        var apariciones = System.Text.RegularExpressions.Regex.Matches(contenido, System.Text.RegularExpressions.Regex.Escape(ComentarioDeExclusion)).Count;
        var llamadasAAbrirAsync = System.Text.RegularExpressions.Regex.Matches(contenido, @"\.AbrirAsync\(").Count;

        apariciones.Should().Be(llamadasAAbrirAsync,
            "cada AbrirAsync de este archivo (firma y sello) debe llevar su propia explicación de por qué no registra");
    }

    private static string LeerArchivo(string rutaRelativa)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), rutaRelativa.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(ruta).Should().BeTrue($"{rutaRelativa} debería existir — si se movió o renombró, actualiza este ratchet");
        return File.ReadAllText(ruta);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
