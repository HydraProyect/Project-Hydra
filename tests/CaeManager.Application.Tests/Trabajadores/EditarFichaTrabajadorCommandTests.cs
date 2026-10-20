using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class EditarFichaTrabajadorCommandTests
{
    private static Trabajador Crear() => Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z",
        email: "paula@example.test", observaciones: "Conservar", alias: "Pau", telefono: "+34600111222");
    private static EditarFichaTrabajadorCommand Comando(Trabajador t) => new(t.Id, t.Version,
        "41 1234567 89", "Electricista", new(1988, 3, 14), "Colombiana", "Plantilla",
        new(2023, 5, 2), new(2026, 10, 15), "Sí, desde mayo de 2023",
        "Residencia y trabajo por cuenta ajena", new(2027, 2, 18), "Se identifica con NIE");

    private static (EditarFichaTrabajadorCommandHandler Handler, UnitOfWorkFalso Uow) Montar(
        Trabajador? t, AlcanceDatosServiceFalso? alcance = null, params Trabajador[] otros)
    {
        var repo = new TrabajadorRepositorioFalso();
        if (t is not null) repo.Agregar(t);
        foreach (var otro in otros) repo.Agregar(otro);
        var uow = new UnitOfWorkFalso();
        return (new(repo, new Empresas.DelegacionEmpresaYOficioEmpresaTests.CatalogoFalso(), alcance ?? new(), PoliticaNotaInternaPruebas.Con("GestorCae"), uow), uow);
    }

    [Fact]
    public async Task Nombre_documento_y_marca_se_guardan_juntos_con_una_sola_confirmacion()
    {
        var t = Crear(); var (handler, uow) = Montar(t);
        var resultado = await handler.Handle(Comando(t) with
        {
            Nombre = "Marta",
            Apellidos = "Salas",
            Documento = "AB123456",
            ExtranjeroCobrable = new(false)
        }, CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        uow.VecesGuardado.Should().Be(1);
        t.Nombre.Should().Be("Marta"); t.Apellidos.Should().Be("Salas");
        t.Dni.Should().Be("AB123456"); t.MarcaExtranjeroCobrable.Should().BeFalse();
        t.Email.Should().Be("paula@example.test"); t.Alias.Should().Be("Pau");
    }

    [Theory]
    [InlineData("12345678A", true, "Trabajador.DocumentoNoValido")]
    [InlineData("AB123456", false, "Trabajador.DocumentoSinGestion")]
    public async Task Documento_rechazado_no_deja_el_nombre_ni_la_ficha_modificados(string documento, bool gestiona, string codigo)
    {
        var t = Crear();
        var (handler, uow) = Montar(t, new(empresaIdsParaGestion: gestiona ? null : []));
        var resultado = await handler.Handle(Comando(t) with
        {
            Nombre = "Marta",
            Documento = documento
        }, CancellationToken.None);
        resultado.Error.Codigo.Should().Be(codigo);
        uow.VecesGuardado.Should().Be(0);
        t.Nombre.Should().Be("Paula"); t.Dni.Should().Be("12345678Z");
        t.NumeroAfiliacionSeguridadSocial.Should().BeNull();
    }

    [Fact]
    public async Task Cambiar_el_documento_no_reimpone_la_marca_a_mano_guardada()
    {
        var t = Crear(); t.FijarMarcaExtranjeroCobrable(true);
        var (handler, uow) = Montar(t);
        var resultado = await handler.Handle(Comando(t) with
        {
            Documento = "12345679S",
            ExtranjeroCobrable = new(null)
        }, CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue(); uow.VecesGuardado.Should().Be(1);
        t.Dni.Should().Be("12345679S");
        t.MarcaExtranjeroCobrable.Should().BeNull();
    }

    [Fact]
    public async Task Un_documento_que_contradice_lo_que_decide_el_documento_se_fija_a_mano()
    {
        var t = Crear();
        var (handler, uow) = Montar(t);
        var resultado = await handler.Handle(Comando(t) with
        {
            Documento = "X1234567L",
            ExtranjeroCobrable = new(true)
        }, CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue(); uow.VecesGuardado.Should().Be(1);
        t.Dni.Should().Be("X1234567L");
        t.MarcaExtranjeroCobrable.Should().BeTrue();
    }

    [Fact]
    public async Task Documento_de_otro_trabajador_responde_DniDuplicado_y_no_toca_la_ficha()
    {
        var t = Crear();
        var otro = Trabajador.DeEmpresa(Guid.NewGuid(), "Otra", "Persona", "12345679S");
        var (handler, uow) = Montar(t, null, otro);
        var resultado = await handler.Handle(Comando(t) with
        {
            Nombre = "Marta",
            Documento = "12345679S"
        }, CancellationToken.None);
        resultado.Error.Codigo.Should().Be("Trabajador.DniDuplicado");
        uow.VecesGuardado.Should().Be(0);
        t.Dni.Should().Be("12345678Z"); t.Nombre.Should().Be("Paula");
    }

    [Fact]
    public async Task El_mismo_documento_normalizado_no_exige_gestion()
    {
        var t = Crear(); var (handler, uow) = Montar(t, new(empresaIdsParaGestion: []));
        var resultado = await handler.Handle(Comando(t) with
        {
            Nombre = "Marta",
            Documento = "12345678Z"
        }, CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue(); uow.VecesGuardado.Should().Be(1);
        t.Nombre.Should().Be("Marta"); t.Dni.Should().Be("12345678Z");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "Trabajador.DocumentoSinGestion")]
    public async Task La_subcontrata_gestiona_la_correccion_de_su_documento(bool gestiona, string? codigo)
    {
        var subcontrataId = Guid.NewGuid();
        var t = Trabajador.DeSubcontrata(subcontrataId, "Paula", "Campos", "12345678Z");
        var (handler, uow) = Montar(t, new(subcontrataIdsParaGestion: gestiona ? [subcontrataId] : []));
        var resultado = await handler.Handle(Comando(t) with { Documento = "12345679S" }, CancellationToken.None);
        if (codigo is null)
        {
            resultado.EsExitoso.Should().BeTrue(); uow.VecesGuardado.Should().Be(1);
            t.Dni.Should().Be("12345679S");
        }
        else
        {
            resultado.Error.Codigo.Should().Be(codigo); uow.VecesGuardado.Should().Be(0);
            t.Dni.Should().Be("12345678Z");
        }
    }

    [Fact]
    public async Task Guarda_solo_la_ficha_y_conserva_los_demas_datos()
    {
        var t = Crear(); var (handler, uow) = Montar(t); var c = Comando(t);
        var resultado = await handler.Handle(c, CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue(); uow.VecesGuardado.Should().Be(1);
        t.NumeroAfiliacionSeguridadSocial.Should().Be("41123456789");
        t.Puesto.Should().Be(c.Puesto); t.FechaNacimiento.Should().Be(c.FechaNacimiento);
        t.Nacionalidad.Should().Be(c.Nacionalidad); t.TipoVinculo.Should().Be(c.TipoVinculo);
        t.FechaAltaEmpresa.Should().Be(c.FechaAltaEmpresa); t.FechaBajaEmpresa.Should().Be(c.FechaBajaEmpresa);
        t.FiguraEnRnt.Should().Be(c.FiguraEnRnt); t.TipoPermisoTrabajo.Should().Be(c.TipoPermisoTrabajo);
        t.CaducidadPermisoTrabajo.Should().Be(c.CaducidadPermisoTrabajo); t.MotivoPermisoTrabajo.Should().Be(c.MotivoPermisoTrabajo);
        t.Nombre.Should().Be("Paula"); t.Apellidos.Should().Be("Campos"); t.Dni.Should().Be("12345678Z");
        t.Email.Should().Be("paula@example.test"); t.Observaciones.Should().Be("Conservar");
        t.Alias.Should().Be("Pau"); t.Telefono.Should().Be("+34600111222");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_encontrado_no_guarda_ni_modifica_si_no_existe_o_no_es_visible(bool existe)
    {
        var t = Crear(); var (handler, uow) = Montar(existe ? t : null, new(trabajadorIdsVisibles: []));
        var resultado = await handler.Handle(Comando(t), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
        t.NumeroAfiliacionSeguridadSocial.Should().BeNull(); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_version_antigua_no_modifica_ni_guarda()
    {
        var t = Crear(); var (handler, uow) = Montar(t);
        var resultado = await handler.Handle(Comando(t) with { Version = Guid.NewGuid() }, CancellationToken.None);
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        t.NumeroAfiliacionSeguridadSocial.Should().BeNull(); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_persona_anonimizada_no_recupera_datos_personales()
    {
        var t = Crear(); t.Anonimizar(DateTime.UtcNow); var (handler, uow) = Montar(t);
        var resultado = await handler.Handle(Comando(t), CancellationToken.None);
        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("Trabajador.Anonimizado");
        t.Nacionalidad.Should().BeNull(); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_exige_identidad_version_formato_limites_y_orden_de_fechas()
    {
        var validator = new EditarFichaTrabajadorCommandValidator(); var c = Comando(Crear());
        validator.Validate(c).IsValid.Should().BeTrue();
        var largo = new string('x', Trabajador.LongitudMaximaCampoFicha + 1);
        var resultado = validator.Validate(c with
        {
            Id = Guid.Empty,
            Version = Guid.Empty,
            NumeroAfiliacionSeguridadSocial = "no es un número",
            Puesto = largo,
            Nacionalidad = largo,
            TipoVinculo = largo,
            FiguraEnRnt = largo,
            TipoPermisoTrabajo = largo,
            MotivoPermisoTrabajo = largo,
            FechaBajaEmpresa = new(2023, 5, 1)
        });
        resultado.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(
            ["Id", "Version", "NumeroAfiliacionSeguridadSocial", "Puesto", "Nacionalidad", "TipoVinculo",
             "FiguraEnRnt", "TipoPermisoTrabajo", "MotivoPermisoTrabajo", "FechaBajaEmpresa"]);
        validator.Validate(c with
        {
            NumeroAfiliacionSeguridadSocial = null,
            Puesto = null,
            FechaNacimiento = null,
            Nacionalidad = null,
            TipoVinculo = null,
            FechaAltaEmpresa = null,
            FechaBajaEmpresa = null,
            FiguraEnRnt = null,
            TipoPermisoTrabajo = null,
            CaducidadPermisoTrabajo = null,
            MotivoPermisoTrabajo = null
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_tipo_de_contratacion_la_ficha_no_lo_cambia_y_con_tipo_lo_guarda()
    {
        var t = Crear(); t.CambiarTipoContratacion(TipoContratacion.Autonomo); var (handler, _) = Montar(t);
        (await handler.Handle(Comando(t), CancellationToken.None)).EsExitoso.Should().BeTrue();
        t.TipoContratacion.Should().Be(TipoContratacion.Autonomo, "null significa que el formulario no lo toca");

        (await handler.Handle(Comando(t) with { TipoContratacion = TipoContratacion.Voluntario }, CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        t.TipoContratacion.Should().Be(TipoContratacion.Voluntario);
    }

    [Fact]
    public void Los_validadores_rechazan_un_tipo_de_contratacion_fuera_del_enum()
    {
        var ficha = Comando(Crear());
        new EditarFichaTrabajadorCommandValidator().Validate(ficha with { TipoContratacion = TipoContratacion.Voluntario })
            .IsValid.Should().BeTrue();
        new EditarFichaTrabajadorCommandValidator().Validate(ficha with { TipoContratacion = (TipoContratacion)99 })
            .Errors.Should().ContainSingle(e => e.PropertyName == "TipoContratacion.Value" || e.PropertyName == "TipoContratacion");

        var alta = new CrearTrabajadorCommand(Guid.NewGuid(), null, "Ana", "Ruiz", "12345678Z", null, null, null);
        new CrearTrabajadorCommandValidator().Validate(alta with { TipoContratacion = TipoContratacion.Voluntario })
            .IsValid.Should().BeTrue();
        new CrearTrabajadorCommandValidator().Validate(alta with { TipoContratacion = (TipoContratacion)99 })
            .Errors.Should().ContainSingle(e => e.PropertyName == "TipoContratacion");
    }
}
