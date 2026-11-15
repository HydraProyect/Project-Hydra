using CaeManager.Application.Tenants.Encargo;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Toda área de Application con peticiones está clasificada para el Encargo de administración</b>
/// (decisión D-8, 2026-10-08).
///
/// <para>
/// Quien administra por encargo lleva rol efectivo Administrador o Dirección CAE, y la mayoría de los
/// handlers no comprueban el rol: lo que no esté excluido en <see cref="ActosExcluidosDelEncargo"/>
/// queda abierto. Este trinquete obliga a que un área nueva se declare permitida o excluida al
/// escribirla, en vez de nacer abierta sin que nadie lo haya decidido.
/// </para>
///
/// <para>
/// <b>Lo que observa</b>: por reflexión, todo tipo concreto y cerrado del ensamblado de Application que
/// es una petición de MediatR (<see cref="IBaseRequest"/> o <see cref="IStreamRequest{TResponse}"/>), y su
/// área (<see cref="ActosExcluidosDelEncargo.AreaDe(Type)"/>: la <see cref="AreaDelEncargoAttribute"/> del
/// tipo o la de una base marcada; nunca la carpeta. Una petición sin área resuelta falla cerrado en
/// ejecución y aquí da rojo). Las genéricas
/// abiertas no se instancian; las vigila <c>AreaDelEncargoPorTipoTests</c>.
/// <b>Lo que NO observa</b>: si la clasificación es acertada —eso es una decisión—; una petición nueva
/// dentro de un área ya permitida pasa este test y la para la lista congelada.
/// </para>
/// </summary>
public class AreasClasificadasParaElEncargoTests
{
    private static readonly List<Type> Peticiones = typeof(ActosExcluidosDelEncargo).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                    && (typeof(IBaseRequest).IsAssignableFrom(t)
                        || t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>))))
        .ToList();

    [Fact]
    public void Toda_area_con_peticiones_esta_clasificada_y_ninguna_clasificacion_sobra()
    {
        Peticiones.Should().HaveCountGreaterThan(300, "control positivo: el detector ve las peticiones de Application");

        var areasConPeticiones = Peticiones
            .Select(p => ActosExcluidosDelEncargo.AreaDe(p)
                         ?? throw new InvalidOperationException(
                             $"{p.FullName} no tiene área: no lleva [AreaDelEncargo] con un área clasificada. Bajo encargo "
                             + "no pleno queda excluida (falla cerrado); declara su área en el tipo y clasifica el área si es nueva."))
            .ToHashSet(StringComparer.Ordinal);

        ActosExcluidosDelEncargo.AreasExcluidas.Should().NotIntersectWith(ActosExcluidosDelEncargo.AreasPermitidas,
            "un área está excluida entera o permitida; lo que se excluye de un área permitida va por tipo");

        areasConPeticiones.Should().BeEquivalentTo(
            ActosExcluidosDelEncargo.AreasExcluidas.Concat(ActosExcluidosDelEncargo.AreasPermitidas),
            "un área nueva con peticiones se clasifica en ActosExcluidosDelEncargo en el mismo commit: sin eso nace "
            + "abierta para quien administra por encargo; y un área que ya no tiene peticiones se retira de la lista");
    }

    [Fact]
    public void Las_peticiones_excluidas_por_tipo_existen_y_pertenecen_a_un_area_permitida()
    {
        ActosExcluidosDelEncargo.PeticionesExcluidas.Should().OnlyContain(t => Peticiones.Contains(t),
            "la lista nombra peticiones reales de Application");

        ActosExcluidosDelEncargo.PeticionesExcluidas
            .Select(t => ActosExcluidosDelEncargo.AreaDe(t))
            .Should().OnlyContain(area => area != null && ActosExcluidosDelEncargo.AreasPermitidas.Contains(area),
                "una petición de un área ya excluida entera no necesita nombrarse: nombrarla escondería que el área lo está");
    }

    [Fact]
    public void Las_areas_excluidas_cierran_todas_sus_peticiones_y_solo_esas()
    {
        var excluidas = Peticiones
            .Where(p => ActosExcluidosDelEncargo.Excluye(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(p)))
            .ToList();

        var porArea = Peticiones
            .Where(p => ActosExcluidosDelEncargo.AreasExcluidas.Contains(ActosExcluidosDelEncargo.AreaDe(p)!))
            .ToList();
        porArea.Should().HaveCountGreaterThan(15, "control positivo: ApiKeys, Facturacion, Importacion y Retencion tienen peticiones");

        // ObtenerAuditoriaQuery sin inicializar lleva EntidadId nulo: es su variante excluida.
        excluidas.Should().BeEquivalentTo(
            porArea.Concat(ActosExcluidosDelEncargo.PeticionesExcluidas)
                .Append(typeof(CaeManager.Application.Auditoria.Queries.ObtenerAuditoriaQuery)));
    }

    /// <summary>
    /// El catálogo base de Plataforma (B-13) está permitido bajo encargo porque hoy solo se lee: lo mantiene un Actor
    /// de Plataforma TALVEG, nunca quien administra un Tenant por encargo. Una petición nueva del área nacería
    /// permitida (ver el resumen de la clase); este test obliga a que la primera que no sea de lectura se excluya por
    /// tipo en <see cref="ActosExcluidosDelEncargo.PeticionesExcluidas"/> o reabra la clasificación en el mismo commit.
    /// <b>Lo que NO observa</b>: distingue lectura de escritura por el sufijo <c>Query</c> del nombre, la convención
    /// del repositorio; un Command mal llamado «Query» pasaría.
    /// </summary>
    [Fact]
    public void El_catalogo_base_solo_queda_permitido_para_leer()
    {
        var delCatalogo = Peticiones.Where(p => ActosExcluidosDelEncargo.AreaDe(p) == "CatalogoBase").ToList();
        delCatalogo.Should().NotBeEmpty("control positivo: ObtenerCatalogoBaseQuery vive en el área");

        delCatalogo.Should().OnlyContain(
            p => p.Name.EndsWith("Query", StringComparison.Ordinal) || ActosExcluidosDelEncargo.PeticionesExcluidas.Contains(p),
            "mantener el catálogo base no es acto de quien administra un Tenant por encargo: una escritura del área "
            + "se excluye por tipo o se decide otra clasificación en el commit que la crea");
    }

    /// <summary>
    /// Codex r2 (#2241): el área ya no sale de la carpeta. Un tipo en la carpeta de un área clasificada, sin marca, no
    /// tiene área; antes de la ronda 3 <c>ActosExcluidosDelEncargo</c> tomaba «Tenants» de su espacio de nombres.
    /// </summary>
    [Fact]
    public void El_area_no_sale_de_la_carpeta()
    {
        ActosExcluidosDelEncargo.AreaDe(typeof(CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi.ObtenerClavesApiQuery))
            .Should().Be("ApiKeys");
        typeof(ActosExcluidosDelEncargo).Namespace.Should().StartWith("CaeManager.Application.Tenants");
        ActosExcluidosDelEncargo.AreasPermitidas.Should().Contain("Tenants");
        ActosExcluidosDelEncargo.AreaDe(typeof(ActosExcluidosDelEncargo)).Should().BeNull(
            "vive en la carpeta de un área permitida, pero sin marca no tiene área");
        ActosExcluidosDelEncargo.AreaDe(typeof(string)).Should().BeNull("fuera de Application no hay área");
    }
}
