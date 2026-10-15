using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class ObtenerFichaTrabajadorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_detalle_proyecta_la_ficha_completa_para_ambos_vinculos_de_empleador(bool deSubcontrata)
    {
        var empresa = Empresa.CrearComoSubcontrata("Montajes", null, "Supervisado");
        var t = deSubcontrata
            ? Trabajador.DeSubcontrata(empresa.Id, "Paula", "Campos", "12345678Z")
            : Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos", "12345678Z");
        t.ActualizarFicha("41 1234567 89", "Electricista", new(1988, 3, 14), "Colombiana", "Plantilla",
            new(2023, 5, 2), new(2026, 10, 15), "Sí, desde mayo de 2023",
            "Residencia y trabajo por cuenta ajena", new(2027, 2, 18), "Se identifica con NIE");
        var empresas = new EmpresasQueryContextFalso(); empresas.ListaEmpresas.Add(empresa);
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        var handler = new ObtenerTrabajadorPorIdQueryHandler(empresas, trabajadores, new AlcanceDatosServiceFalso(),
            PoliticaNotaInternaPruebas.Con("GestorCae"));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(t.Id); dto.Version.Should().Be(t.Version); dto.EmpleadorNombre.Should().Be("Montajes");
        dto.NumeroAfiliacionSeguridadSocial.Should().Be(t.NumeroAfiliacionSeguridadSocial);
        dto.Puesto.Should().Be(t.Puesto); dto.FechaNacimiento.Should().Be(t.FechaNacimiento);
        dto.Nacionalidad.Should().Be(t.Nacionalidad); dto.TipoVinculo.Should().Be(t.TipoVinculo);
        dto.FechaAltaEmpresa.Should().Be(t.FechaAltaEmpresa); dto.FechaBajaEmpresa.Should().Be(t.FechaBajaEmpresa);
        dto.FiguraEnRnt.Should().Be(t.FiguraEnRnt); dto.TipoPermisoTrabajo.Should().Be(t.TipoPermisoTrabajo);
        dto.CaducidadPermisoTrabajo.Should().Be(t.CaducidadPermisoTrabajo); dto.MotivoPermisoTrabajo.Should().Be(t.MotivoPermisoTrabajo);
        dto.PermisoTrabajoVisible.Should().BeTrue();
    }

    private static (Trabajador Trabajador, ObtenerTrabajadorPorIdQueryHandler Handler) ConFichaCompleta(
        IPoliticaNotaInterna politica)
    {
        var empresa = new Empresa("Montajes", "B12345674");
        var t = Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos", "12345678Z");
        t.ActualizarFicha("41 1234567 89", "Electricista", new(1988, 3, 14), "Colombiana", "Plantilla",
            new(2023, 5, 2), null, "Sí, desde mayo de 2023",
            "Residencia y trabajo por cuenta ajena", new(2027, 2, 18), "Se identifica con NIE");
        var empresas = new EmpresasQueryContextFalso(); empresas.ListaEmpresas.Add(empresa);
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        return (t, new ObtenerTrabajadorPorIdQueryHandler(empresas, trabajadores, new AlcanceDatosServiceFalso(), politica));
    }

    // Decisión de Chris del 2026-10-15: el Usuario de Cliente que ve al Trabajador por una Asignación no recibe el
    // permiso de trabajo ni su motivo; la afiliación y el vínculo, sí. Se mide en el DTO, que es lo que sale del servidor.
    [Fact]
    public async Task El_usuario_de_cliente_recibe_la_ficha_sin_el_permiso_de_trabajo_pero_con_afiliacion_y_vinculo()
    {
        var (t, handler) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con("Cliente"));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.PermisoTrabajoVisible.Should().BeFalse();
        dto.TipoPermisoTrabajo.Should().BeNull();
        dto.CaducidadPermisoTrabajo.Should().BeNull();
        dto.MotivoPermisoTrabajo.Should().BeNull();
        dto.NumeroAfiliacionSeguridadSocial.Should().Be(t.NumeroAfiliacionSeguridadSocial).And.NotBeNull();
        dto.TipoVinculo.Should().Be("Plantilla");
        dto.FechaAltaEmpresa.Should().Be(new DateOnly(2023, 5, 2));
        dto.FiguraEnRnt.Should().Be("Sí, desde mayo de 2023");
        dto.Nacionalidad.Should().Be("Colombiana");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_de_gestion_recibe_el_permiso_de_trabajo(string rol)
    {
        var (t, handler) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto!.PermisoTrabajoVisible.Should().BeTrue();
        dto.TipoPermisoTrabajo.Should().Be("Residencia y trabajo por cuenta ajena");
        dto.CaducidadPermisoTrabajo.Should().Be(new DateOnly(2027, 2, 18));
        dto.MotivoPermisoTrabajo.Should().Be("Se identifica con NIE");
    }

    /// <summary>
    /// La ficha es read-modify-write: el Drawer de edición precarga el permiso desde este DTO y
    /// <c>EditarFichaTrabajadorCommand</c> lo reescribe entero. Quien puede guardar la ficha tiene que recibir el
    /// permiso al leer; si no, «Guardar» lo borraría sin haberlo visto. Rol a rol de la lista real de
    /// <c>AutorizacionEscrituraBehavior</c>: un rol de escritura nuevo que esta consulta no entregue pone el test en rojo.
    /// </summary>
    [Fact]
    public async Task Todo_rol_que_puede_guardar_la_ficha_recibe_el_permiso_de_trabajo_al_leer()
    {
        var rolesConEscritura = AutorizacionEscrituraBehavior<EditarFichaTrabajadorCommand, Result>.RolesConEscritura;
        rolesConEscritura.Should().NotBeEmpty("con la lista real vacía, el bucle de abajo no mediría nada");

        foreach (var rol in rolesConEscritura)
        {
            var (t, handler) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con(rol));
            var dto = await handler.Handle(new(t.Id), CancellationToken.None);
            dto!.PermisoTrabajoVisible.Should().BeTrue($"el rol {rol} puede guardar la ficha y la reescribe entera");
            dto.MotivoPermisoTrabajo.Should().Be("Se identifica con NIE", $"el rol {rol} puede guardar la ficha");
        }
    }

    [Fact]
    public async Task La_sesion_privilegiada_con_acceso_total_recibe_el_permiso_y_la_impersonacion_no()
    {
        var (t, conSoporte) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con(null,
            PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.SoporteLectura)));
        (await conSoporte.Handle(new(t.Id), CancellationToken.None))!.MotivoPermisoTrabajo.Should().Be("Se identifica con NIE");

        var (t2, conImpersonacion) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con("GestorCae",
            PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));
        var dto = await conImpersonacion.Handle(new(t2.Id), CancellationToken.None);
        dto!.PermisoTrabajoVisible.Should().BeFalse("la impersonación falla cerrada (decisión del 2026-10-11)");
        dto.TipoPermisoTrabajo.Should().BeNull();
    }

    [Fact]
    public async Task El_detalle_invisible_no_devuelve_datos_personales()
    {
        var t = Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z");
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        var handler = new ObtenerTrabajadorPorIdQueryHandler(new EmpresasQueryContextFalso(), trabajadores,
            new AlcanceDatosServiceFalso(trabajadorIdsVisibles: []), PoliticaNotaInternaPruebas.Con("GestorCae"));
        (await handler.Handle(new(t.Id), CancellationToken.None)).Should().BeNull();
    }
}
