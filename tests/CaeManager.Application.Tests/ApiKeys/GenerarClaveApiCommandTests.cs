using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Application.Tests.Tenants;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.ApiKeys;

public class GenerarClaveApiCommandTests
{
    [Theory]
    [InlineData("sin autoridad", "ClaveApi.NoAutorizado")]
    [InlineData("otro objetivo", "ClaveApi.NoAutorizado")]
    [InlineData("sin 2FA", "ClaveApi.SinDobleFactor")]
    [InlineData("sin motivo", "ClaveApi.DatosInvalidos")]
    [InlineData("motivo en blanco", "ClaveApi.DatosInvalidos")]
    [InlineData("inactiva", "ClaveApi.DelegacionNoVigente")]
    [InlineData("vencida", "ClaveApi.DelegacionNoVigente")]
    [InlineData("sin caducidad", "ClaveApi.DatosInvalidos")]
    [InlineData("caducidad pasada", "ClaveApi.DatosInvalidos")]
    [InlineData("mas de 365 dias", "ClaveApi.DatosInvalidos")]
    [InlineData("otro origen", "DelegacionTenant.NoEncontrada")]
    [InlineData("proposito comercial", "DelegacionTenant.NoEncontrada")]
    public async Task Rechaza_la_emision_si_falta_una_precondicion(string caso, string codigo)
    {
        var origen = Guid.NewGuid();
        var objetivo = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var delegacion = DelegacionTenant.ParaSoporte(origen, objetivo);
        if (caso != "inactiva")
            delegacion.ActivarParaSoporte("Revisión de la integración", caso == "vencida" ? ahora.AddHours(-1) : ahora.AddHours(1), ahora.AddHours(-2));
        if (caso == "proposito comercial") delegacion = new DelegacionTenant(origen, objetivo);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);
        var claves = new RepositorioClaves();
        var guardado = new UnitOfWorkFalso();
        var autorizacion = caso == "sin autoridad" ? AutorizacionAdminPlataformaFalsa.SinNada()
            : AutorizacionAdminPlataformaFalsa.AcotadaA(caso == "otro objetivo" ? Guid.NewGuid() : objetivo);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: caso == "otro origen" ? Guid.NewGuid() : origen,
            tieneDobleFactorActivo: caso != "sin 2FA");
        var peticion = new GenerarClaveApiCommand(delegacion.Id, "Integración Alfa", ahora.AddDays(30), "Revisión de la integración");
        peticion = caso switch
        {
            "sin motivo" => peticion with { Motivo = "" },
            "motivo en blanco" => peticion with { Motivo = "   " },
            "sin caducidad" => peticion with { CaducaEnUtc = null },
            "caducidad pasada" => peticion with { CaducaEnUtc = ahora.AddDays(-1) },
            "mas de 365 dias" => peticion with { CaducaEnUtc = ahora.AddDays(366) },
            _ => peticion
        };

        var resultado = await new GenerarClaveApiCommandHandler(delegaciones, claves, autorizacion, usuario, guardado)
            .Handle(peticion, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
        claves.Claves.Should().BeEmpty();
        guardado.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Emite_con_autoridad_acotada_2FA_motivo_y_vigencias_y_guarda_en_el_Tenant_propietario_objetivo()
    {
        var origen = Guid.NewGuid();
        var objetivo = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var delegacion = DelegacionTenant.ParaSoporte(origen, objetivo);
        delegacion.ActivarParaSoporte("Revisión de la integración", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);
        var claves = new RepositorioClaves();
        var autorizacion = AutorizacionAdminPlataformaFalsa.AcotadaA(objetivo);
        var guardado = new UnitOfWorkFalso();
        var peticion = new GenerarClaveApiCommand(delegacion.Id, "Integración Alfa", DateTime.UtcNow.AddDays(365), " Motivo registrado ");

        var resultado = await new GenerarClaveApiCommandHandler(delegaciones, claves, autorizacion,
            new CurrentUserServiceFalso(usuarioId, tenantOrigenId: origen), guardado).Handle(peticion, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        autorizacion.UltimoTenantConsultado.Should().Be(objetivo);
        autorizacion.SeConsultoLoGlobal.Should().BeFalse();
        var clave = claves.Claves.Should().ContainSingle().Subject;
        clave.MotivoEmision.Should().Be("Motivo registrado");
        clave.CaducaEnUtc.Should().Be(peticion.CaducaEnUtc);
        clave.CreadaPorUsuarioId.Should().Be(usuarioId);
        claves.TenantAlAgregar.Should().Be(objetivo);
        guardado.VecesGuardado.Should().Be(1);
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
        clave.HashClave.Should().NotBe(resultado.Valor.ClaveEnClaro);
    }

    private sealed class RepositorioClaves : IClaveApiRepository
    {
        public List<ClaveApi> Claves { get; } = [];
        public Guid? TenantAlAgregar { get; private set; }
        public void Agregar(ClaveApi clave) { TenantAlAgregar = AmbitoTenantExplicito.TenantIdActual; Claves.Add(clave); }
        public Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ClaveSigueRespaldadaAsync(Guid tenantPropietarioId, Guid creadaPorUsuarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
