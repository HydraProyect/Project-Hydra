using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// REC-061: hasta este cambio, el dominio de Plantillas (ADR-010) tenía cero
/// filas en toda la siembra de demo — <c>CicloDocumentalDatosPruebaSeeder.SembrarPlantillasAsync</c>
/// cierra ese hueco. Este fichero MIDE que produce exactamente los estados
/// que motivaron el cambio: una plantilla confirmada (con la que se genera)
/// y una en Borrador, más un <see cref="DocumentoGenerado"/> en cada uno de
/// los dos estados de <see cref="EstadoDocumentoGenerado"/> — el "con avisos"
/// es justo el que <c>DocumentosGeneradosPanel.razor</c> (DEC-5, 2026-09-02)
/// necesita para poder revisarse.
/// </summary>
public class PlantillasDatosPruebaSeederTests
{
    [Fact]
    public async Task Siembra_una_plantilla_confirmada_y_una_en_borrador_con_documentos_generados_en_los_dos_estados()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        var tenantId = await CrearTenantConCatalogoAsync(contexto);

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            await roleManager.CreateAsync(new IdentityRole<Guid>(Roles.GestorCae));
            var gestor = new ApplicationUser
            {
                UserName = "gestor.plantillas@caemanager.local",
                Email = "gestor.plantillas@caemanager.local",
                NombreCompleto = "Gestor de prueba",
                EmailConfirmed = true,
                TenantId = tenantId,
            };
            (await userManager.CreateAsync(gestor, "Prueba#2026")).Succeeded.Should().BeTrue();
            await userManager.AddToRoleAsync(gestor, Roles.GestorCae);

            var empresa = new Empresa("Empresa base para plantillas");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajadores = new List<Trabajador>();
            for (var numero = 1; numero <= 12; numero++)
            {
                var trabajador = Trabajador.DeEmpresa(empresa.Id, "Nombre", $"Apellido {numero:D2}", GenerarDniValido(numero));
                trabajadores.Add(trabajador);
                contexto.Trabajadores.Add(trabajador);
            }
            await contexto.SaveChangesAsync();

            var tipoEpi = await contexto.TiposDocumento.SingleAsync(t => t.Nombre == "Entrega de EPI");
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
            var documentosTrabajador = trabajadores
                .Select(t => Documento.DeTrabajador(t.Id, tipoEpi.Id, hoy.AddMonths(-1), hoy.AddMonths(11)))
                .ToList();
            contexto.Documentos.AddRange(documentosTrabajador);
            await contexto.SaveChangesAsync();

            await CicloDocumentalDatosPruebaSeeder.SembrarPlantillasAsync(
                contexto, userManager, documentosTrabajador, CancellationToken.None);
            await contexto.SaveChangesAsync();

            var plantillas = await contexto.PlantillasDocumento.ToListAsync();
            plantillas.Should().HaveCount(2, "MEDIDO: una confirmada (con la que se genera) y una en Borrador");

            var versiones = await contexto.PlantillasDocumentoVersion.ToListAsync();
            var confirmadas = versiones.Where(v => v.EstadoConfiguracion == EstadoConfiguracionPlantilla.Confirmada).ToList();
            confirmadas.Should().ContainSingle("MEDIDO: solo la plantilla operativa está confirmada");
            confirmadas[0].Elementos.Should().HaveCount(4,
                "MEDIDO: tres campos de dato obligatorios más la firma del trabajador");

            var borradores = versiones.Where(v => v.EstadoConfiguracion != EstadoConfiguracionPlantilla.Confirmada).ToList();
            borradores.Should().ContainSingle("MEDIDO: la segunda plantilla se queda sin confirmar a propósito");

            var generados = await contexto.DocumentosGenerados.ToListAsync();
            generados.Should().HaveCount(2, "MEDIDO: uno sin avisos y uno con avisos (DEC-5)");
            generados.Should().Contain(g => g.Estado == EstadoDocumentoGenerado.Generado);
            generados.Should().Contain(g => g.Estado == EstadoDocumentoGenerado.GeneradoConAvisos);
            generados.Select(g => g.TrabajadorId).Should().OnlyHaveUniqueItems(
                "MEDIDO: cada DocumentoGenerado reutiliza un Trabajador distinto de los ya sembrados");
            generados.Select(g => g.PlantillaDocumentoVersionId).Should().AllBeEquivalentTo(confirmadas[0].Id,
                "MEDIDO: los dos documentos generados vienen de la versión confirmada, nunca del Borrador");
        }
    }

    private static string GenerarDniValido(int numero)
    {
        const string letras = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letras[numero % 23]}";
    }

    private static async Task<Guid> CrearTenantConCatalogoAsync(CaeManagerDbContext contexto)
    {
        var tenant = new Tenant($"Tenant plantillas {Guid.NewGuid():N}");
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            contexto.Tenants.Add(tenant);
            contexto.ParametrosSistema.Add(new ParametroSistema(
                ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));
            contexto.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());
            await contexto.SaveChangesAsync();
        }

        return tenant.Id;
    }
}
