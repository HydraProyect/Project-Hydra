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
/// <b>Lo que observa</b>: por reflexión, todo tipo concreto del ensamblado de Application que
/// implementa <see cref="IBaseRequest"/>, y su área (el segmento de su espacio de nombres tras
/// <c>CaeManager.Application.</c>). <b>Lo que NO observa</b>: si la clasificación es acertada —eso es
/// una decisión—, ni una petición nueva dentro de un área ya permitida, que nace permitida.
/// </para>
/// </summary>
public class AreasClasificadasParaElEncargoTests
{
    private static readonly List<Type> Peticiones = typeof(ActosExcluidosDelEncargo).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IBaseRequest).IsAssignableFrom(t))
        .ToList();

    [Fact]
    public void Toda_area_con_peticiones_esta_clasificada_y_ninguna_clasificacion_sobra()
    {
        Peticiones.Should().HaveCountGreaterThan(300, "control positivo: el detector ve las peticiones de Application");

        var areasConPeticiones = Peticiones
            .Select(p => ActosExcluidosDelEncargo.AreaDe(p)
                         ?? throw new InvalidOperationException($"{p.FullName} no vive bajo CaeManager.Application."))
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

    [Fact]
    public void El_area_es_el_segmento_exacto_tras_la_raiz_y_no_un_prefijo()
    {
        ActosExcluidosDelEncargo.AreaDe(typeof(CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi.ObtenerClavesApiQuery))
            .Should().Be("ApiKeys");
        ActosExcluidosDelEncargo.AreaDe(typeof(ActosExcluidosDelEncargo)).Should().Be("Tenants");
        ActosExcluidosDelEncargo.AreaDe(typeof(string)).Should().BeNull("fuera de Application no hay área");
        ActosExcluidosDelEncargo.AreaDe(typeof(AreasClasificadasParaElEncargoTests)).Should().BeNull(
            "CaeManager.Architecture.Tests no empieza por la raíz de Application");
    }
}
