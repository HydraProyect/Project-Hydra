using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Catálogo revisado el 2026-10-17: todo tipo semilla de Trabajador y de Empresa pertenece a un grupo
/// (<see cref="GrupoTipoDocumento"/>), que es como se presenta el catálogo. Un tipo nuevo añadido a
/// <see cref="TipoDocumentoSeedData.Datos"/> sin grupo nacería suelto en todos los Tenants: este ratchet lo impide.
/// </summary>
public class GruposDelCatalogoSemillaTests
{
    [Fact]
    public void Todo_tipo_de_Trabajador_y_de_Empresa_tiene_grupo()
    {
        var sinGrupo = TipoDocumentoSeedData.Datos
            .Where(d => d.Ambito is AmbitoAplicacion.Trabajador or AmbitoAplicacion.Empresa)
            .Where(d => TipoDocumentoSeedData.GrupoDe(d.Id) is null)
            .Select(d => d.Nombre)
            .ToList();

        sinGrupo.Should().BeEmpty("cada tipo semilla de Trabajador y de Empresa tiene que llevar grupo");
    }

    [Fact]
    public void Las_copias_por_Tenant_heredan_grupo_y_archivan_contra_la_copia_del_sustituto()
    {
        var copias = TipoDocumentoSeedData.CrearCopiasParaTenant().ToList();

        copias.Where(c => c.AmbitoAplicacion is AmbitoAplicacion.Trabajador or AmbitoAplicacion.Empresa)
            .Should().OnlyContain(c => c.Grupo != null);

        var porId = copias.ToDictionary(c => c.Id);
        var archivados = copias.Where(c => c.Archivado).ToList();
        archivados.Select(c => c.Nombre).Should().BeEquivalentTo(
            "Recibo de pago RLC/TC1", "RLC/TC1 + Recibo de pago", "Formación EPIs",
            "Protocolo frente al acoso sexual y por razón de sexo");
        archivados.Should().OnlyContain(c => c.Requerido == RequisitoDocumental.No);
        foreach (var archivado in archivados.Where(c => c.SustitutoId != null))
            porId.Should().ContainKey(archivado.SustitutoId!.Value, "el sustituto es la copia del mismo Tenant, no la fila base");
    }
}
