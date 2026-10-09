using CaeManager.Application.Configuracion.Commands.GuardarOrdenCajasFicha;
using CaeManager.Application.Configuracion.Commands.RestablecerOrdenCajasFicha;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// Reglas de Application del orden de cajas de las fichas 360: de quién es la
/// fila que se escribe, se borra y se lee. El aislamiento por Tenant y el
/// repositorio real se prueban en IntegrationTests
/// (<c>OrdenCajasFichaBajoRlsTests</c>); aquí el repositorio es falso.
/// </summary>
public class OrdenCajasFichaCommandsTests
{
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Guid _otroUsuario = Guid.NewGuid();
    private readonly OrdenCajasFichaRepositorioFalso _repositorio = new();

    [Fact]
    public async Task Guarda_el_orden_para_el_usuario_actual()
    {
        var resultado = await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");

        resultado.EsExitoso.Should().BeTrue();
        _repositorio.Ordenes.Should().ContainSingle()
            .Which.Should().Match<OrdenCajasFicha>(o => o.UsuarioId == _usuario && o.TipoFicha == TiposDeFicha360.Empresa);
        _repositorio.Ordenes[0].Claves.Should().Equal("notas", "contacto");
    }

    [Fact]
    public async Task Guardar_otra_vez_sustituye_el_orden_y_no_crea_otra_fila()
    {
        await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");

        await Guardar(_usuario, TiposDeFicha360.Empresa, "contacto", "notas", "datos-fiscales");

        _repositorio.Ordenes.Should().ContainSingle().Which.Claves.Should().Equal("contacto", "notas", "datos-fiscales");
    }

    [Fact]
    public async Task Cada_tipo_de_ficha_tiene_su_propio_orden()
    {
        await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");
        await Guardar(_usuario, TiposDeFicha360.Centro, "accesos", "contacto");

        (await Leer(_usuario, TiposDeFicha360.Empresa)).Should().Equal("notas", "contacto");
        (await Leer(_usuario, TiposDeFicha360.Centro)).Should().Equal("accesos", "contacto");
    }

    [Fact]
    public async Task Guardar_no_toca_el_orden_de_otro_usuario()
    {
        await Guardar(_otroUsuario, TiposDeFicha360.Empresa, "contacto", "notas");

        await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");

        _repositorio.Ordenes.Should().HaveCount(2);
        _repositorio.Ordenes.Single(o => o.UsuarioId == _otroUsuario).Claves.Should().Equal("contacto", "notas");
    }

    [Fact]
    public async Task Guardar_falla_sin_usuario_identificado()
    {
        var resultado = await Guardar(null, TiposDeFicha360.Empresa, "notas");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OrdenCajasFicha.SinUsuario");
        _repositorio.Ordenes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_Tenant_activo_no_se_guarda_y_el_fallo_es_legible(bool tenantVacio)
    {
        var handler = new GuardarOrdenCajasFichaCommandHandler(
            new CurrentUserServiceFalso(_usuario), new TenantActualFijo(tenantVacio ? Guid.Empty : null),
            _repositorio, new Clientes.UnitOfWorkFalso(), TimeProvider.System);

        var resultado = await handler.Handle(
            new GuardarOrdenCajasFichaCommand(TiposDeFicha360.Empresa, ["notas"]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OrdenCajasFicha.SinTenant");
        _repositorio.Ordenes.Should().BeEmpty();
    }

    [Fact]
    public async Task Restablecer_borra_el_orden_del_usuario_actual_y_la_lectura_queda_vacia()
    {
        await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");

        var resultado = await Restablecer(_usuario, TiposDeFicha360.Empresa);

        resultado.EsExitoso.Should().BeTrue();
        _repositorio.Ordenes.Should().BeEmpty();
        (await Leer(_usuario, TiposDeFicha360.Empresa)).Should().BeEmpty();
    }

    [Fact]
    public async Task Restablecer_no_borra_el_orden_de_otro_usuario_ni_el_de_otro_tipo_de_ficha()
    {
        await Guardar(_otroUsuario, TiposDeFicha360.Empresa, "contacto", "notas");
        await Guardar(_usuario, TiposDeFicha360.Centro, "accesos", "contacto");
        await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");

        await Restablecer(_usuario, TiposDeFicha360.Empresa);

        _repositorio.Ordenes.Select(o => (o.UsuarioId, o.TipoFicha)).Should().BeEquivalentTo(
            [(_otroUsuario, TiposDeFicha360.Empresa), (_usuario, TiposDeFicha360.Centro)]);
    }

    [Fact]
    public async Task Restablecer_sin_orden_guardado_termina_bien_y_no_guarda_nada()
    {
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new RestablecerOrdenCajasFichaCommandHandler(new CurrentUserServiceFalso(_usuario), _repositorio, unitOfWork);

        var resultado = await handler.Handle(new RestablecerOrdenCajasFichaCommand(TiposDeFicha360.Empresa), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Restablecer_falla_sin_usuario_identificado()
    {
        var resultado = await Restablecer(null, TiposDeFicha360.Empresa);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OrdenCajasFicha.SinUsuario");
    }

    [Fact]
    public async Task La_lectura_devuelve_solo_el_orden_del_usuario_actual()
    {
        await Guardar(_otroUsuario, TiposDeFicha360.Empresa, "contacto", "notas");

        (await Leer(_usuario, TiposDeFicha360.Empresa)).Should().BeEmpty("el único orden guardado es de otro usuario");

        await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");

        (await Leer(_usuario, TiposDeFicha360.Empresa)).Should().Equal("notas", "contacto");
        (await Leer(_otroUsuario, TiposDeFicha360.Empresa)).Should().Equal("contacto", "notas");
    }

    [Fact]
    public async Task La_lectura_sin_usuario_identificado_devuelve_vacio()
    {
        await Guardar(_usuario, TiposDeFicha360.Empresa, "notas", "contacto");

        (await Leer(null, TiposDeFicha360.Empresa)).Should().BeEmpty();
    }

    [Fact]
    public void El_validador_de_guardar_acepta_cada_tipo_de_ficha_admitido()
    {
        var validador = new GuardarOrdenCajasFichaCommandValidator();

        TiposDeFicha360.Admitidos.Should().HaveCount(9).And.OnlyHaveUniqueItems();
        foreach (var tipo in TiposDeFicha360.Admitidos)
            validador.Validate(new GuardarOrdenCajasFichaCommand(tipo, ["notas", "contacto"])).IsValid.Should().BeTrue(tipo);
    }

    [Theory]
    [InlineData("Documento")]
    [InlineData("empresa")]
    [InlineData("")]
    public void Los_validadores_rechazan_un_tipo_de_ficha_que_no_esta_en_la_lista(string tipoFicha)
    {
        new GuardarOrdenCajasFichaCommandValidator()
            .Validate(new GuardarOrdenCajasFichaCommand(tipoFicha, ["notas"])).IsValid.Should().BeFalse();
        new RestablecerOrdenCajasFichaCommandValidator()
            .Validate(new RestablecerOrdenCajasFichaCommand(tipoFicha)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void El_validador_de_guardar_rechaza_listas_vacias_repetidas_o_con_claves_mal_formadas()
    {
        var validador = new GuardarOrdenCajasFichaCommandValidator();

        validador.Validate(new GuardarOrdenCajasFichaCommand(TiposDeFicha360.Empresa, [])).IsValid.Should().BeFalse();
        validador.Validate(new GuardarOrdenCajasFichaCommand(TiposDeFicha360.Empresa, null!)).IsValid.Should().BeFalse();
        validador.Validate(new GuardarOrdenCajasFichaCommand(TiposDeFicha360.Empresa, ["notas", "notas"])).IsValid.Should().BeFalse();
        validador.Validate(new GuardarOrdenCajasFichaCommand(TiposDeFicha360.Empresa, ["Notas internas"])).IsValid.Should().BeFalse();
    }

    private Task<Domain.Common.Result> Guardar(Guid? usuario, string tipoFicha, params string[] claves) =>
        new GuardarOrdenCajasFichaCommandHandler(
                new CurrentUserServiceFalso(usuario), new TenantActualFijo(Guid.NewGuid()),
                _repositorio, new Clientes.UnitOfWorkFalso(), TimeProvider.System)
            .Handle(new GuardarOrdenCajasFichaCommand(tipoFicha, claves), CancellationToken.None);

    private Task<Domain.Common.Result> Restablecer(Guid? usuario, string tipoFicha) =>
        new RestablecerOrdenCajasFichaCommandHandler(new CurrentUserServiceFalso(usuario), _repositorio, new Clientes.UnitOfWorkFalso())
            .Handle(new RestablecerOrdenCajasFichaCommand(tipoFicha), CancellationToken.None);

    private Task<IReadOnlyList<string>> Leer(Guid? usuario, string tipoFicha)
    {
        var contexto = new ConfiguracionQueryContextFalso();
        contexto.ListaOrdenesCajasFicha.AddRange(_repositorio.Ordenes);
        return new ObtenerOrdenCajasFichaQueryHandler(contexto, new CurrentUserServiceFalso(usuario))
            .Handle(new ObtenerOrdenCajasFichaQuery(tipoFicha), CancellationToken.None);
    }

    private sealed class OrdenCajasFichaRepositorioFalso : IOrdenCajasFichaRepository
    {
        public List<OrdenCajasFicha> Ordenes { get; } = [];

        public Task<OrdenCajasFicha?> ObtenerAsync(Guid usuarioId, string tipoFicha, CancellationToken cancellationToken = default) =>
            Task.FromResult(Ordenes.FirstOrDefault(o => o.UsuarioId == usuarioId && o.TipoFicha == tipoFicha));

        public void Agregar(OrdenCajasFicha orden) => Ordenes.Add(orden);

        public void Eliminar(OrdenCajasFicha orden) => Ordenes.Remove(orden);
    }
}
