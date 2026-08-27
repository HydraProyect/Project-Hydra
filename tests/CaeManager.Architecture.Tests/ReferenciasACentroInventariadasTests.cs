using System.Reflection;
using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// F5 va a partir <c>Centro</c> en <c>CentroTrabajo</c> (la ubicación física
/// del titular) + <c>ParticipacionEmpresaEnCentro</c>, y eso obliga a
/// re-anclar todo lo que hoy apunta a un Centro.
///
/// El instrumento natural para inventariar eso —buscar las claves foráneas—
/// tiene un falso negativo: <b>cuatro entidades llevan una columna
/// <c>CentroId</c> sin FK declarada</b>, así que no aparecen ni en las
/// configuraciones de EF, ni en el snapshot de migraciones, ni en un error de
/// compilación cuando el tipo cambie. Contar solo FKs da 11; el alcance real
/// es 15.
///
/// Este ratchet no exige que las cuatro tengan FK —esa es una decisión de
/// diseño aparte, y una de ellas es deliberadamente laxa— sino que
/// <b>ninguna referencia a Centro pueda aparecer sin quedar inventariada</b>.
/// Si mañana alguien añade una quinta columna <c>CentroId</c> sin FK, este
/// test se pone rojo y obliga a clasificarla antes de que F5 la barra por
/// alto.
/// </summary>
public class ReferenciasACentroInventariadasTests
{
    /// <summary>
    /// Entidades de dominio con una propiedad <c>CentroId</c> <b>sin</b> clave
    /// foránea declarada hacia <c>Centro</c>. Verificado a mano contra
    /// <c>src/CaeManager.Infrastructure/Persistence/Configurations/</c>
    /// (2026-08-27): once configuraciones declaran <c>HasOne&lt;Centro&gt;()</c>
    /// y ninguna de estas cuatro está entre ellas.
    ///
    /// Añadir aquí una entrada nueva es una decisión deliberada, no un
    /// trámite: significa aceptar que la base de datos no impedirá que esa
    /// columna apunte a un Centro inexistente o de otro tenant.
    /// </summary>
    private static readonly HashSet<string> ReferenciasSinClaveForanea =
    [
        "SugerenciaVisitaCorreo",
        "SolicitudPrioridadDocumento",
        "DocumentoGenerado",
        "PlantillaDocumento",
    ];

    /// <summary>
    /// Entidades de dominio con una propiedad <c>CentroId</c> <b>y</b> clave
    /// foránea declarada. Once, todas con <c>DeleteBehavior.Restrict</c> salvo
    /// <c>VerificacionExternaSubcontrata</c>, que es <c>Cascade</c> — matiz que
    /// F5 tendrá que resolver al decidir a cuál de las dos entidades nuevas se
    /// hereda ese comportamiento.
    /// </summary>
    private static readonly HashSet<string> ReferenciasConClaveForanea =
    [
        "Asignacion",
        "CanalGestionDocumental",
        "ContactoAgenda",
        "TipoDocumentoCentro",
        "Gestion",
        "Incidencia",
        "AsignacionCartera",
        "AsignacionOperacion",
        "Proyecto",
        "VerificacionExternaSubcontrata",
        "Visita",
    ];

    [Fact]
    public void Toda_referencia_a_Centro_desde_el_dominio_esta_inventariada()
    {
        var inventariadas = ReferenciasConClaveForanea.Union(ReferenciasSinClaveForanea).ToHashSet();

        var encontradas = TiposConReferenciaACentro().Select(t => t.Name).ToHashSet();

        var sinInventariar = encontradas.Except(inventariadas).OrderBy(n => n).ToList();

        sinInventariar.Should().BeEmpty(
            "F5 re-ancla todo lo que apunta a un Centro, y una referencia que no esté en este " +
            "inventario se le pasará por alto — sobre todo si no tiene FK, porque entonces no " +
            "aparece ni en las configuraciones de EF ni en el snapshot. Clasifícala en " +
            "ReferenciasConClaveForanea o en ReferenciasSinClaveForanea, en este mismo commit");
    }

    [Fact]
    public void El_inventario_no_arrastra_entradas_muertas()
    {
        // La otra mitad, que un ratchet de solo "actual ⊆ permitido" no
        // detectaría: si una entidad se retira o deja de referenciar a Centro,
        // su entrada aquí se queda mintiendo. Es exactamente el modo de fallo
        // que ya arrastra la lista blanca de FronterasEntrePersistenciaDeFeaturesTests.
        var inventariadas = ReferenciasConClaveForanea.Union(ReferenciasSinClaveForanea).ToHashSet();

        var encontradas = TiposConReferenciaACentro().Select(t => t.Name).ToHashSet();

        var fantasmas = inventariadas.Except(encontradas).OrderBy(n => n).ToList();

        fantasmas.Should().BeEmpty(
            "estas entradas del inventario ya no corresponden a ninguna entidad con CentroId — " +
            "retíralas para que el inventario siga describiendo el presente");
    }

    [Fact]
    public void El_instrumento_encuentra_de_verdad_las_referencias()
    {
        // Validación del propio instrumento: si la reflexión dejara de
        // encontrar tipos (cambio de ensamblado, de nombre de propiedad,
        // filtro demasiado estrecho), los dos tests de arriba pasarían en
        // verde por vacío — "no encontré nada fuera del inventario" es
        // indistinguible de "no encontré nada". Este fija el suelo.
        var encontradas = TiposConReferenciaACentro().ToList();

        encontradas.Should().HaveCountGreaterThanOrEqualTo(15,
            "hoy hay 11 referencias con FK y 4 sin ella; si el recuento cae por debajo, " +
            "lo más probable es que el instrumento haya dejado de observar, no que las " +
            "referencias hayan desaparecido");
    }

    /// <summary>
    /// Tipos del ensamblado de dominio con una propiedad que termina en
    /// <c>CentroId</c>. Se busca por nombre de propiedad y no por FK, que es
    /// precisamente lo que hace que encuentre las cuatro que no la tienen.
    ///
    /// <b>Termina en</b>, no <b>es igual a</b>: la primera versión de este
    /// método buscaba el nombre exacto <c>CentroId</c> y se dejaba fuera
    /// <c>AsignacionCartera</c> y <c>AsignacionOperacion</c>, cuya columna es
    /// <c>AmbitoCentroId</c>. Lo detectó
    /// <see cref="El_instrumento_encuentra_de_verdad_las_referencias"/>, que
    /// existe exactamente para eso: un filtro demasiado estrecho deja los
    /// otros dos tests en verde por vacío.
    /// </summary>
    private static IEnumerable<Type> TiposConReferenciaACentro() =>
        typeof(Centro).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p => p.Name.EndsWith("CentroId", StringComparison.Ordinal)));
}
