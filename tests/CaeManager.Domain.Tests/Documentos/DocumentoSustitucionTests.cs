using System.Reflection;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Documento efectivo, PR 1: la sustitución explícita (<see cref="Documento.SustituirPor"/>) y la expresión del
/// documento operativo (<see cref="DocumentoOperativo"/>). Propiedades de dominio: las que garantiza el agregado. La
/// barrera de la base (FK compuesta con el Tenant, CHECK de coherencia) la prueba PostgreSQL real en
/// <c>AnadeSustitucionDeDocumentosTests</c> (IntegrationTests) y no presta evidencia a esta clase.
/// </summary>
public class DocumentoSustitucionTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private static readonly DateTime Ahora = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tipo = Guid.NewGuid();

    private static Documento DeTrabajador(Guid trabajador, Guid? tipo = null, int emitidoHaceDias = 30) =>
        Documento.DeTrabajador(trabajador, tipo ?? Tipo, Hoy.AddDays(-emitidoHaceDias), VigenciaDocumento.VenceEl(Hoy.AddYears(1)),
            archivoUrl: $"archivos/{Guid.NewGuid()}.pdf");

    private static void SellarTenant(Documento documento, Guid tenant) =>
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(documento, tenant);

    private static void ForzarId(Documento documento, Guid id) =>
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(documento, id);

    // ── Qué hace ───────────────────────────────────────────────────────────

    [Fact]
    public void Un_documento_nuevo_es_operativo_y_no_tiene_datos_de_sustitucion()
    {
        var documento = DeTrabajador(Guid.NewGuid());

        documento.EstaSustituido.Should().BeFalse();
        documento.SustituidoPorDocumentoId.Should().BeNull();
        documento.SustituidoEnUtc.Should().BeNull();
        documento.MotivoSustitucion.Should().BeNull();
        DocumentoOperativo.Es(documento).Should().BeTrue();
    }

    [Theory]
    [InlineData(MotivoSustitucionDocumento.Renovacion)]
    [InlineData(MotivoSustitucionDocumento.SubidaNueva)]
    [InlineData(MotivoSustitucionDocumento.ClasificacionDeDatosExistentes)]
    public void Sustituir_registra_quien_cuando_y_por_que_y_el_sustituto_sigue_operativo(MotivoSustitucionDocumento motivo)
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador, emitidoHaceDias: 400);
        var nuevo = DeTrabajador(trabajador);

        anterior.SustituirPor(nuevo, motivo, Ahora);

        anterior.EstaSustituido.Should().BeTrue();
        anterior.SustituidoPorDocumentoId.Should().Be(nuevo.Id);
        anterior.SustituidoEnUtc.Should().Be(Ahora);
        anterior.MotivoSustitucion.Should().Be(motivo);
        DocumentoOperativo.Es(anterior).Should().BeFalse("el sustituido es historial");
        DocumentoOperativo.Es(nuevo).Should().BeTrue("el sustituto es el que cuenta");
        nuevo.EstaSustituido.Should().BeFalse("sustituir no toca al sustituto");
    }

    [Fact]
    public void Sustituir_nunca_borra_ni_toca_lo_que_el_sustituido_conserva()
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador, emitidoHaceDias: 400);
        var (id, archivo, emision, vencimiento, estado) =
            (anterior.Id, anterior.ArchivoUrl, anterior.FechaEmision, anterior.FechaVencimiento, anterior.EstadoVigencia);

        anterior.SustituirPor(DeTrabajador(trabajador), MotivoSustitucionDocumento.Renovacion, Ahora);

        anterior.Id.Should().Be(id, "D8: el sustituido conserva su identidad histórica");
        anterior.ArchivoUrl.Should().Be(archivo, "decisión 7: el archivo anterior no se borra");
        anterior.FechaEmision.Should().Be(emision);
        anterior.FechaVencimiento.Should().Be(vencimiento);
        anterior.EstadoVigencia.Should().Be(estado);
        anterior.EstaEliminado.Should().BeFalse();
        anterior.AnonimizadoEnUtc.Should().BeNull();
    }

    [Fact]
    public void El_sustituto_siempre_tiene_otro_Id_y_los_Ids_nuevos_no_se_repiten()
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var nuevo = DeTrabajador(trabajador);

        nuevo.Id.Should().NotBe(anterior.Id, "D8: renovar crea un registro con Id propio");
        Enumerable.Range(0, 200).Select(_ => DeTrabajador(trabajador).Id).Distinct().Should().HaveCount(200);

        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora);
        anterior.SustituidoPorDocumentoId.Should().NotBe(anterior.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Un_sustituto_mas_antiguo_o_mas_reciente_es_igual_de_valido_la_fecha_no_decide(bool sustitutoMasAntiguo)
    {
        // D5: subir un documento anterior al que está en uso se expresa sustituyendo al NUEVO por el que ya estaba:
        // el antiguo nace en el historial y nunca pasa a efectivo por subirlo.
        var trabajador = Guid.NewGuid();
        var enUso = DeTrabajador(trabajador, emitidoHaceDias: 30);
        var otro = DeTrabajador(trabajador, emitidoHaceDias: sustitutoMasAntiguo ? 400 : 5);

        var accion = () => otro.SustituirPor(enUso, MotivoSustitucionDocumento.SubidaNueva, Ahora);

        accion.Should().NotThrow();
        DocumentoOperativo.Es(otro).Should().BeFalse();
        DocumentoOperativo.Es(enUso).Should().BeTrue();
    }

    // ── Invariantes ────────────────────────────────────────────────────────

    [Fact]
    public void Un_documento_no_puede_sustituirse_a_si_mismo()
    {
        var documento = DeTrabajador(Guid.NewGuid());

        var accion = () => documento.SustituirPor(documento, MotivoSustitucionDocumento.Renovacion, Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*a sí mismo*");
        documento.EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public void Otro_objeto_con_el_mismo_Id_tampoco_es_un_sustituto()
    {
        var trabajador = Guid.NewGuid();
        var documento = DeTrabajador(trabajador);
        var copia = DeTrabajador(trabajador);
        ForzarId(copia, documento.Id);

        var accion = () => documento.SustituirPor(copia, MotivoSustitucionDocumento.Renovacion, Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*a sí mismo*", "D8: no se reutilizan Ids");
        documento.EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public void Un_documento_no_se_sustituye_dos_veces_y_el_primer_sustituto_se_conserva()
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador, emitidoHaceDias: 400);
        var primero = DeTrabajador(trabajador, emitidoHaceDias: 30);
        var segundo = DeTrabajador(trabajador, emitidoHaceDias: 5);
        anterior.SustituirPor(primero, MotivoSustitucionDocumento.Renovacion, Ahora);

        var accion = () => anterior.SustituirPor(segundo, MotivoSustitucionDocumento.SubidaNueva, Ahora.AddHours(1));

        accion.Should().Throw<InvalidOperationException>().WithMessage("*ya está sustituido*");
        anterior.SustituidoPorDocumentoId.Should().Be(primero.Id);
        anterior.SustituidoEnUtc.Should().Be(Ahora);
        anterior.MotivoSustitucion.Should().Be(MotivoSustitucionDocumento.Renovacion);
    }

    [Fact]
    public void Un_sustituto_ya_sustituido_no_puede_ocupar_el_lugar_de_otro_y_no_hay_ciclos()
    {
        var trabajador = Guid.NewGuid();
        var a = DeTrabajador(trabajador, emitidoHaceDias: 400);
        var b = DeTrabajador(trabajador, emitidoHaceDias: 30);
        a.SustituirPor(b, MotivoSustitucionDocumento.Renovacion, Ahora);

        var cierraCiclo = () => b.SustituirPor(a, MotivoSustitucionDocumento.Renovacion, Ahora);
        var usaElSustituido = () => DeTrabajador(trabajador).SustituirPor(a, MotivoSustitucionDocumento.Renovacion, Ahora);

        cierraCiclo.Should().Throw<InvalidOperationException>("a ya está sustituido: no puede sustituir a b");
        usaElSustituido.Should().Throw<InvalidOperationException>("a es historial: nada lo sustituye");
        b.EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public void Una_cadena_de_renovaciones_deja_un_solo_operativo()
    {
        var trabajador = Guid.NewGuid();
        var primero = DeTrabajador(trabajador, emitidoHaceDias: 800);
        var segundo = DeTrabajador(trabajador, emitidoHaceDias: 400);
        var tercero = DeTrabajador(trabajador, emitidoHaceDias: 30);

        primero.SustituirPor(segundo, MotivoSustitucionDocumento.Renovacion, Ahora.AddDays(-400));
        segundo.SustituirPor(tercero, MotivoSustitucionDocumento.Renovacion, Ahora);

        new[] { primero, segundo, tercero }.Where(DocumentoOperativo.Es).Should().ContainSingle().Which.Should().BeSameAs(tercero);
    }

    [Fact]
    public void El_sustituto_tiene_que_ser_del_mismo_Tipo_de_documento()
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var deOtroTipo = DeTrabajador(trabajador, tipo: Guid.NewGuid());

        var accion = () => anterior.SustituirPor(deOtroTipo, MotivoSustitucionDocumento.Renovacion, Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*mismo Tipo*");
        anterior.EstaSustituido.Should().BeFalse();
    }

    public static IEnumerable<object[]> TitularesDistintos()
    {
        var tipo = Tipo;
        Documento Trabajador(Guid id) => Documento.DeTrabajador(id, tipo, Hoy, VigenciaDocumento.NoCaduca);
        Documento Cliente(Guid id) => Documento.DeCliente(id, tipo, Hoy, VigenciaDocumento.NoCaduca);
        Documento Empresa(Guid id) => Documento.DeEmpresa(id, tipo, Hoy, VigenciaDocumento.NoCaduca);
        Documento Vehiculo(Guid id) => Documento.DeVehiculo(id, tipo, Hoy, VigenciaDocumento.NoCaduca);
        Documento Proyecto(Guid id) => Documento.DeProyecto(id, tipo, Hoy, VigenciaDocumento.NoCaduca);

        var x = Guid.NewGuid();
        yield return [(Func<Documento>)(() => Trabajador(x)), (Func<Documento>)(() => Trabajador(Guid.NewGuid())), "otro Trabajador"];
        yield return [(Func<Documento>)(() => Cliente(x)), (Func<Documento>)(() => Cliente(Guid.NewGuid())), "otro Cliente"];
        yield return [(Func<Documento>)(() => Empresa(x)), (Func<Documento>)(() => Empresa(Guid.NewGuid())), "otra Empresa"];
        yield return [(Func<Documento>)(() => Vehiculo(x)), (Func<Documento>)(() => Vehiculo(Guid.NewGuid())), "otro Vehículo"];
        yield return [(Func<Documento>)(() => Proyecto(x)), (Func<Documento>)(() => Proyecto(Guid.NewGuid())), "otro Proyecto"];
        // El mismo Guid como titular de otra clase de propietario: el titular es (clase, Id), no solo el Id.
        yield return [(Func<Documento>)(() => Trabajador(x)), (Func<Documento>)(() => Empresa(x)), "mismo Id, otra clase de titular"];
        yield return [(Func<Documento>)(() => Empresa(x)), (Func<Documento>)(() => Cliente(x)), "mismo Id, Empresa frente a Cliente"];
    }

    [Theory]
    [MemberData(nameof(TitularesDistintos))]
    public void El_sustituto_tiene_que_tener_el_mismo_titular(Func<Documento> anterior, Func<Documento> nuevo, string caso)
    {
        var sustituido = anterior();

        var accion = () => sustituido.SustituirPor(nuevo(), MotivoSustitucionDocumento.Renovacion, Ahora);

        accion.Should().Throw<ArgumentException>($"{caso}").WithMessage("*mismo titular*");
        sustituido.EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public void El_sustituto_tiene_que_ser_del_mismo_Tenant_propietario_cuando_los_dos_lo_tienen()
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var nuevo = DeTrabajador(trabajador);
        SellarTenant(anterior, Guid.NewGuid());
        SellarTenant(nuevo, Guid.NewGuid());

        var accion = () => anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*mismo Tenant propietario*");
        anterior.EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public void Con_el_mismo_Tenant_sellado_la_sustitucion_se_acepta_control_positivo()
    {
        var trabajador = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var nuevo = DeTrabajador(trabajador);
        SellarTenant(anterior, tenant);
        SellarTenant(nuevo, tenant);

        anterior.Invoking(a => a.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora)).Should().NotThrow();
    }

    [Fact]
    public void Un_sustituto_recien_creado_sin_Tenant_sellado_se_acepta_porque_la_barrera_es_la_FK_de_la_base()
    {
        // El interceptor sella el Tenant al guardar. Antes de eso Guid.Empty no es «otro Tenant».
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var nuevo = DeTrabajador(trabajador);
        SellarTenant(anterior, Guid.NewGuid());

        anterior.Invoking(a => a.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora)).Should().NotThrow();
    }

    [Fact]
    public void Un_sustituido_sin_Tenant_sellado_acepta_un_sustituto_ya_sellado()
    {
        // Simétrico del anterior: el sustituido aún sin sellar y el sustituto ya sellado también se aceptan.
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var nuevo = DeTrabajador(trabajador);
        SellarTenant(nuevo, Guid.NewGuid());

        anterior.Invoking(a => a.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora)).Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Un_documento_eliminado_ni_sustituye_ni_es_sustituido(bool eliminadoElSustituido)
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var nuevo = DeTrabajador(trabajador);
        (eliminadoElSustituido ? anterior : nuevo).MarcarComoEliminado(Guid.NewGuid());

        var accion = () => anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora);

        accion.Should().Throw<InvalidOperationException>().WithMessage("*eliminado*");
        anterior.EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public void Los_argumentos_invalidos_se_rechazan_sin_dejar_el_documento_a_medias()
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador);
        var nuevo = DeTrabajador(trabajador);

        anterior.Invoking(a => a.SustituirPor(null!, MotivoSustitucionDocumento.Renovacion, Ahora))
            .Should().Throw<ArgumentNullException>();
        anterior.Invoking(a => a.SustituirPor(nuevo, (MotivoSustitucionDocumento)0, Ahora))
            .Should().Throw<ArgumentOutOfRangeException>("cero no es un motivo: «sin motivo» es la columna nula");
        anterior.Invoking(a => a.SustituirPor(nuevo, (MotivoSustitucionDocumento)99, Ahora))
            .Should().Throw<ArgumentOutOfRangeException>();
        anterior.Invoking(a => a.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.SpecifyKind(Ahora, DateTimeKind.Local)))
            .Should().Throw<ArgumentException>().WithMessage("*UTC*");
        anterior.Invoking(a => a.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.SpecifyKind(Ahora, DateTimeKind.Unspecified)))
            .Should().Throw<ArgumentException>().WithMessage("*UTC*");

        anterior.SustituidoPorDocumentoId.Should().BeNull();
        anterior.SustituidoEnUtc.Should().BeNull();
        anterior.MotivoSustitucion.Should().BeNull();
    }

    // ── D5: el sustituido nunca vuelve a ser operativo ─────────────────────

    [Fact]
    public void Un_sustituido_no_vuelve_a_ser_operativo_ni_eliminandolo_y_restaurandolo_ni_corrigiendo_su_vigencia()
    {
        var trabajador = Guid.NewGuid();
        var anterior = DeTrabajador(trabajador, emitidoHaceDias: 400);
        var nuevo = DeTrabajador(trabajador);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, Ahora);

        anterior.MarcarComoEliminado(Guid.NewGuid());
        anterior.Restaurar();
        // CorregirVigencia no rechaza hoy un sustituido (si el historial es inmutable lo decide el PR 4); lo que se
        // afirma aquí es solo que corregirlo no lo devuelve a operativo.
        anterior.CorregirVigencia(Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(2)));

        anterior.EstaSustituido.Should().BeTrue();
        anterior.SustituidoPorDocumentoId.Should().Be(nuevo.Id);
        DocumentoOperativo.Es(anterior).Should().BeFalse("D5: ni siquiera con fechas más recientes que las del sustituto");
    }

    [Fact]
    public void Ningun_metodo_publico_del_agregado_deshace_una_sustitucion()
    {
        // Barrera estructural: las tres columnas solo se escriben dentro de SustituirPor. Si alguien añade un
        // setter o un método que las anule, este test lo ve (D5: el sustituido no vuelve a ser operativo).
        foreach (var nombre in new[] { nameof(Documento.SustituidoPorDocumentoId), nameof(Documento.SustituidoEnUtc), nameof(Documento.MotivoSustitucion) })
        {
            var propiedad = typeof(Documento).GetProperty(nombre)!;
            propiedad.SetMethod.Should().NotBeNull("EF necesita escribirla");
            propiedad.SetMethod!.IsPublic.Should().BeFalse($"{nombre} solo se escribe desde SustituirPor");
        }

        typeof(Documento).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n.Contains("Sustitu", StringComparison.Ordinal))
            .Should().BeEquivalentTo(["get_SustituidoPorDocumentoId", "get_SustituidoEnUtc", "get_MotivoSustitucion", "get_EstaSustituido", "SustituirPor"]);
    }

    // ── Corregir no es renovar ─────────────────────────────────────────────

    [Fact]
    public void Corregir_la_vigencia_cambia_el_mismo_registro_sin_sustituir_nada()
    {
        var documento = DeTrabajador(Guid.NewGuid(), emitidoHaceDias: 400);
        var id = documento.Id;

        documento.CorregirVigencia(Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)));

        documento.Id.Should().Be(id);
        documento.FechaEmision.Should().Be(Hoy);
        documento.EstaSustituido.Should().BeFalse();
        DocumentoOperativo.Es(documento).Should().BeTrue();
    }

    [Fact]
    public void El_metodo_Renovar_del_dominio_ya_no_existe()
    {
        // «Renovar» queda para el comando que crea un registro nuevo y sustituye al anterior (PR 4 del diseño);
        // el método de dominio que solo pisaba fechas se llama CorregirVigencia. Que nadie lo reintroduzca con el
        // nombre viejo: un lector creería que renovar sigue siendo pisar el mismo registro.
        typeof(Documento).GetMethod("Renovar").Should().BeNull();
        typeof(Documento).GetMethod(nameof(Documento.CorregirVigencia)).Should().NotBeNull();
    }

    // ── DocumentoOperativo ─────────────────────────────────────────────────

    [Fact]
    public void La_expresion_operativa_excluye_sustituidos_y_eliminados_y_coincide_con_Es()
    {
        var trabajador = Guid.NewGuid();
        var operativo = DeTrabajador(trabajador, tipo: Guid.NewGuid());
        var sustituido = DeTrabajador(trabajador, emitidoHaceDias: 400);
        sustituido.SustituirPor(DeTrabajador(trabajador), MotivoSustitucionDocumento.Renovacion, Ahora);
        var eliminado = DeTrabajador(trabajador, tipo: Guid.NewGuid());
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        var sustituidoYEliminado = DeTrabajador(trabajador, tipo: Guid.NewGuid());
        sustituidoYEliminado.SustituirPor(DeTrabajador(trabajador, tipo: sustituidoYEliminado.TipoDocumentoId), MotivoSustitucionDocumento.SubidaNueva, Ahora);
        sustituidoYEliminado.MarcarComoEliminado(Guid.NewGuid());

        var todos = new[] { operativo, sustituido, eliminado, sustituidoYEliminado };
        var porExpresion = todos.AsQueryable().Where(DocumentoOperativo.Expresion).ToList();

        porExpresion.Should().ContainSingle().Which.Should().BeSameAs(operativo);
        todos.Where(DocumentoOperativo.Es).Should().Equal(porExpresion);
    }
}
