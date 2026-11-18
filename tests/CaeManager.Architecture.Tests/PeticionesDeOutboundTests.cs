using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// «Inbound sin Outbound» (decisión del 2026-11-18). Dos ratchets sobre el criterio que cierra Outbound a un Tenant solo
/// Inbound con el papel de Tenant de origen:
/// <list type="number">
/// <item><see cref="PeticionDeOutbound"/>, la copia en tiempo de ejecución del registro de módulos que usan la guarda de
/// comandos y el ámbito del alcance, clasifica cada petición de <c>CaeManager.Application</c> igual que
/// <see cref="RegistroDeModulos"/>. Un comando nuevo en un área de Outbound queda cubierto sin tocar nada; una línea nueva
/// del registro que cambie el módulo de una petición da rojo aquí hasta copiarla.</item>
/// <item>El criterio de papel puro (<see cref="CriterioDePapel.AdmiteLoQueNoEsInbound"/>) solo se llama desde las puertas
/// conocidas. Una puerta nueva de Outbound que lo usara directamente abriría Outbound al Tenant solo Inbound: tiene que
/// pasar por <see cref="ICriterioDeOutbound"/>.</item>
/// </list>
/// Mide por reflexión sobre el ensamblado compilado y por texto sobre <c>src</c>. Lo que no demuestra: que cada puerta
/// niegue (eso lo prueban los tests de la guarda, del handler de páginas, del alcance y del menú).
/// </summary>
public class PeticionesDeOutboundTests
{
    private static readonly Lazy<IReadOnlyList<Type>> Peticiones = new(() =>
        ReflexionArquitecturaHelper.TiposDe(ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application"))
            .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition
                        && typeof(MediatR.IBaseRequest).IsAssignableFrom(t))
            .ToList());

    [Fact]
    public void El_inventario_de_peticiones_de_Outbound_no_esta_vacio()
    {
        // Control positivo del instrumento: si la reflexión dejara de ver las peticiones, la comparación pasaría vacía.
        var registro = RegistroDeModulos.Versionado();
        Peticiones.Value.Count(t => registro.Clasificar(t)?.Modulo == Modulo.Outbound).Should().BeGreaterThan(50);
        Peticiones.Value.Count(t => registro.Clasificar(t)?.Modulo == Modulo.Nucleo).Should().BeGreaterThan(50);
    }

    [Fact]
    public void La_clasificacion_en_tiempo_de_ejecucion_coincide_con_el_registro_de_modulos()
    {
        var registro = RegistroDeModulos.Versionado();
        var divergentes = Peticiones.Value
            .Where(t => PeticionDeOutbound.Es(t) != (registro.Clasificar(t)?.Modulo == Modulo.Outbound))
            .Select(t => $"{t.FullName}: registro={registro.Clasificar(t)?.Modulo}, PeticionDeOutbound={PeticionDeOutbound.Es(t)}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        divergentes.Should().BeEmpty(
            "la guarda de comandos y el ámbito del alcance cierran Outbound al Tenant solo Inbound con PeticionDeOutbound; " +
            "copia ahí la línea del registro que la hace divergir");
    }

    /// <summary>Ficheros de <c>src</c> que pueden llamar al criterio de papel puro, con su motivo.</summary>
    private static readonly Dictionary<string, string> PuertasConocidas = new(StringComparer.Ordinal)
    {
        ["CaeManager.Application/Common/CriterioDePapel.cs"] = "la definición",
        ["CaeManager.Application/Common/CriterioDeOutbound.cs"] = "el criterio de Outbound, que le añade el Producto Contratado",
        ["CaeManager.Infrastructure/Autorizacion/AlcanceDatosService.cs"] =
            "el alcance: lo combina con ICriterioDeOutbound dentro de las peticiones de Outbound y lo usa solo fuera de ellas",
        ["CaeManager.Web/Services/Autorizacion/PaginaDeOutbound.cs"] =
            "la reserva de CriterioDeOutboundEnComponentes cuando la composición no registra ICriterioDeOutbound",
    };

    [Fact]
    public void El_criterio_de_papel_puro_solo_se_usa_desde_las_puertas_conocidas()
    {
        var src = Path.Combine(RaizDelRepositorio(), "src");
        var usos = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("AdmiteLoQueNoEsInbound(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        usos.Should().NotBeEmpty("control del instrumento: la definición misma tiene que aparecer");
        usos.Where(u => !PuertasConocidas.ContainsKey(u)).Should().BeEmpty(
            "una puerta de Outbound tiene que preguntar a ICriterioDeOutbound, que cierra Outbound al Tenant solo Inbound");
        PuertasConocidas.Keys.Where(k => !usos.Contains(k)).Should().BeEmpty("una puerta conocida obsoleta se retira");
    }

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CaeManager.Application")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio desde " + AppContext.BaseDirectory);
    }
}
