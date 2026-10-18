using CaeManager.Application.BusquedaGlobal.Queries.ContarVencenEstaSemana;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.BusquedaGlobal;

/// <summary>
/// «N documentos vencen esta semana» de «Reclamar a {Empresa}» en la paleta, contra PostgreSQL: de hoy al domingo
/// inclusive, Documentos operativos de la Empresa y de sus Trabajadores en cartera; fuera de cartera, null.
/// </summary>
public class ContarVencenEstaSemanaDeEmpresaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _empresa;
    private Guid _trabajadorEnCartera;
    private Guid _trabajadorFueraDeCartera;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var empresa = new Empresa("Montajes Ebro S.L.", "B12345674");
        var otra = new Empresa("Andamios Delta S.L.");
        contexto.Empresas.AddRange(empresa, otra);
        var tipo = new TipoDocumento("Formación PRL", 12, true, 95, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var enCartera = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "12345678Z");
        var fueraDeCartera = Trabajador.DeEmpresa(empresa.Id, "Luis", "Mora", "87654321X");
        var deOtraEmpresa = Trabajador.DeEmpresa(otra.Id, "Eva", "Sanz", "11111111H");
        contexto.Trabajadores.AddRange(enCartera, fueraDeCartera, deOtraEmpresa);
        await contexto.SaveChangesAsync();

        var hoy = DiaDeNegocio.Hoy();
        var domingo = hoy.AddDays((7 - (int)hoy.DayOfWeek) % 7);
        var emision = hoy.AddYears(-1);
        Documento DeTrabajador(Guid id, DateOnly vence) =>
            Documento.DeTrabajador(id, tipo.Id, emision, VigenciaDocumento.VenceEl(vence));

        contexto.Documentos.AddRange(
            Documento.DeEmpresa(empresa.Id, tipo.Id, emision, VigenciaDocumento.VenceEl(hoy)), // cuenta: hoy
            DeTrabajador(enCartera.Id, domingo),                                               // cuenta: el domingo
            DeTrabajador(enCartera.Id, hoy.AddDays(-1)),                                      // ya vencido
            DeTrabajador(enCartera.Id, domingo.AddDays(1)),                                   // lunes siguiente
            DeTrabajador(fueraDeCartera.Id, hoy),                                             // fuera de cartera
            DeTrabajador(deOtraEmpresa.Id, hoy),                                              // otra Empresa
            Documento.DeEmpresa(empresa.Id, tipo.Id, emision, VigenciaDocumento.NoCaduca));   // no caduca
        await contexto.SaveChangesAsync();

        _empresa = empresa.Id;
        _trabajadorEnCartera = enCartera.Id;
        _trabajadorFueraDeCartera = fueraDeCartera.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Cuenta_de_hoy_al_domingo_la_empresa_y_sus_trabajadores_en_cartera()
    {
        var recuento = await ContarAsync(new AlcanceDatosServiceFalso(
            empresaIds: [_empresa], trabajadorIds: [_trabajadorEnCartera]));

        recuento.Should().Be(2, "hoy (Empresa) y el domingo (Trabajador); ni ayer, ni el lunes, ni fuera de cartera");
    }

    [Fact]
    public async Task El_limite_de_la_semana_es_el_domingo_y_el_lunes_queda_fuera()
    {
        // Control: con el Trabajador fuera de cartera también visible suma su documento de hoy; el del lunes
        // siguiente y el de ayer siguen sin contar.
        var recuento = await ContarAsync(new AlcanceDatosServiceFalso(
            empresaIds: [_empresa], trabajadorIds: [_trabajadorEnCartera, _trabajadorFueraDeCartera]));

        recuento.Should().Be(3);
    }

    [Fact]
    public async Task Una_empresa_fuera_de_cartera_no_se_calcula()
    {
        var recuento = await ContarAsync(new AlcanceDatosServiceFalso(
            clienteIds: [], empresaIds: [Guid.NewGuid()], subcontrataIds: [], trabajadorIds: [_trabajadorEnCartera]));

        recuento.Should().BeNull("null es «no calculado», no un 0 que confirme que la Empresa existe");
    }

    private async Task<int?> ContarAsync(AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ContarVencenEstaSemanaDeEmpresaQueryHandler(contexto, contexto, contexto, alcance);
        return await handler.Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresa), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}