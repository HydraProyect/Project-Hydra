using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Todo servicio de fondo declara que quien actúa es la plataforma</b> (P41c).
///
/// <para>
/// Este trinquete existe porque la propiedad que la auditoría promete —"una
/// acción de máquina queda registrada como <c>Sistema</c>"— no la sostiene el
/// interceptor por sí solo: el interceptor sabe leer el ámbito ambiental, pero
/// alguien tiene que establecerlo, y ese alguien es el punto de entrada de cada
/// servicio de fondo. Un servicio nuevo que no lo declare no rompe nada visible:
/// sus filas caen en <c>Desconocido</c>, que es exactamente el hueco que P41c
/// cierra, y ningún test de integración de los existentes se pondría rojo por
/// ello. El fallo sería silencioso — la clase de fallo que un trinquete atrapa y
/// una suite de comportamiento no.
/// </para>
///
/// <para>
/// <b>Lo que este test observa y lo que no.</b> Observa el TEXTO de cada
/// <c>*HostedService.cs</c> de Infrastructure: que dentro del cuerpo de su punto
/// de entrada (<c>ExecuteAsync</c> o <c>StartAsync</c>) aparezca
/// <c>AmbitoActorAuditoria.EstablecerSistema()</c>. No observa que el ámbito
/// llegue de verdad hasta el <c>SaveChanges</c> —eso lo demuestra
/// <c>TipoActorDeAuditoriaBajoRuntimeTests</c> contra PostgreSQL— ni observa los
/// seeders de arranque, que hoy siguen escribiendo con <c>Desconocido</c> (hueco
/// declarado, no olvido: la siembra se invoca desde siete llamadas sueltas de
/// Program.cs y darle su ámbito es un incremento propio).
/// </para>
///
/// <para>
/// <b>Sin lista blanca.</b> Los tres servicios de verificación de arranque
/// (<c>VerificacionKms</c>, <c>VerificacionSignalRRedis</c>,
/// <c>VerificacionDataProtectionS3</c>) no escriben entidades auditadas hoy, y
/// aun así lo declaran: una excepción "porque este no escribe" caducaría el día
/// que escribiera, sin que nada avisara. Vale más una línea de más en tres
/// ficheros que un criterio que hay que reevaluar en cada cambio.
/// </para>
/// </summary>
public class ServiciosDeFondoDeclaranActorDeSistemaTests
{
    private const string Declaracion = "AmbitoActorAuditoria.EstablecerSistema()";

    /// <summary>
    /// El punto de entrada de un <c>BackgroundService</c> o de un
    /// <c>IHostedService</c>. Se busca la declaración DENTRO de su cuerpo y no en
    /// cualquier parte del fichero: puesta en un método auxiliar cualquiera, la
    /// declaración cubriría solo ese camino, y el resto del servicio seguiría
    /// escribiendo como actor desconocido.
    /// </summary>
    private static readonly Regex PuntoDeEntrada = new(
        @"(protected\s+override\s+async\s+Task\s+ExecuteAsync|public\s+async\s+Task\s+StartAsync)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void Todo_servicio_de_fondo_declara_el_ambito_de_actor_de_sistema()
    {
        var servicios = ServiciosDeFondo();

        servicios.Should().NotBeEmpty(
            "si la enumeración no encuentra ningún servicio de fondo el test daría verde sin " +
            "comprobar nada — un resultado vacío no es una ausencia");

        var sinDeclarar = servicios
            .Where(s => !DeclaraEnSuPuntoDeEntrada(s.Texto))
            .Select(s => s.Ruta)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        sinDeclarar.Should().BeEmpty(
            $"cada servicio de fondo tiene que abrir su punto de entrada con {Declaracion}: sin esa " +
            "línea sus escrituras se auditan como Desconocido, indistinguibles de una persona cuya " +
            "identidad no se pudo resolver, que es justo la confusión que P41c deshace");
    }

    /// <summary>
    /// Control positivo del propio instrumento: un texto que declara el ámbito
    /// FUERA del punto de entrada tiene que contarse como infractor. Sin esta
    /// aserción, un <see cref="PuntoDeEntrada"/> que dejara de casar convertiría
    /// el test de arriba en una lista vacía permanente — verde por ceguera.
    /// </summary>
    [Fact]
    public void El_detector_no_acepta_la_declaracion_fuera_del_punto_de_entrada()
    {
        const string fueraDelPuntoDeEntrada = """
            public class Falso : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken ct)
                {
                    await TrabajarAsync(ct);
                }

                private async Task TrabajarAsync(CancellationToken ct)
                {
                    using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
                    await Task.CompletedTask;
                }
            }
            """;

        DeclaraEnSuPuntoDeEntrada(fueraDelPuntoDeEntrada).Should().BeFalse(
            "declararlo en un método auxiliar deja fuera todos los demás caminos del servicio");

        const string dentroDelPuntoDeEntrada = """
            public class Falso : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken ct)
                {
                    using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
                    await TrabajarAsync(ct);
                }
            }
            """;

        DeclaraEnSuPuntoDeEntrada(dentroDelPuntoDeEntrada).Should().BeTrue(
            "y el detector tiene que aceptar la forma correcta, o sería rojo permanente en vez de trinquete");
    }

    /// <summary>
    /// Se mira el fragmento que va desde el punto de entrada hasta el siguiente
    /// —o hasta el final—, que es una aproximación deliberada al cuerpo del
    /// método: delimitar llaves a mano exigiría un analizador, y lo que este
    /// trinquete necesita es detectar la omisión, no validar sintaxis. La
    /// aproximación es conservadora en la dirección correcta: acepta una
    /// declaración en el primer método auxiliar que venga DESPUÉS del punto de
    /// entrada, y el control positivo de arriba fija ese límite por escrito.
    /// </summary>
    private static bool DeclaraEnSuPuntoDeEntrada(string texto)
    {
        var entrada = PuntoDeEntrada.Match(texto);
        if (!entrada.Success) return false;

        var cierreDeFirma = texto.IndexOf(')', entrada.Index + entrada.Length - 1);
        if (cierreDeFirma < 0) return false;

        var aperturaDeCuerpo = texto.IndexOf('{', cierreDeFirma);
        if (aperturaDeCuerpo < 0) return false;

        // Hasta la siguiente declaración de método del mismo nivel de sangrado,
        // que en este repositorio son cuatro espacios.
        var siguienteMiembro = texto.IndexOf("\n    private", aperturaDeCuerpo, StringComparison.Ordinal);
        if (siguienteMiembro < 0)
            siguienteMiembro = texto.IndexOf("\n    protected", aperturaDeCuerpo, StringComparison.Ordinal);
        if (siguienteMiembro < 0)
            siguienteMiembro = texto.Length;

        return texto[aperturaDeCuerpo..siguienteMiembro].Contains(Declaracion, StringComparison.Ordinal);
    }

    private static List<(string Ruta, string Texto)> ServiciosDeFondo()
    {
        var infraestructura = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure");

        return Directory
            .EnumerateFiles(infraestructura, "*HostedService.cs", SearchOption.AllDirectories)
            .Where(ruta => !ruta.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !ruta.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(ruta => (Path.GetRelativePath(RaizDelRepositorio(), ruta), File.ReadAllText(ruta)))
            .ToList();
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
