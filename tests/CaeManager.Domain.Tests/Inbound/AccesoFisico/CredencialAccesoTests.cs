using CaeManager.Domain.Inbound.AccesoFisico;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Inbound.AccesoFisico;

/// <summary>
/// Invariantes y transiciones de la credencial de acceso físico (AF-02): nace Emitida en la serie 1, regenerar sube la serie
/// y solo desde Emitida, revocar y caducar son definitivos y el motivo es un enum cerrado. La base repite lo esencial con
/// los CHECK de CredencialesAcceso (CredencialesAccesoBajoRuntimeTests).
/// </summary>
public class CredencialAccesoTests
{
    private static readonly DateTime Ahora = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Usuario = Guid.NewGuid();

    private static CredencialAcceso.Sujeto Sujeto(string nombre = "Ana Pérez", string? documento = "12345678Z") =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, documento);

    private static CredencialAcceso Emitida() => CredencialAcceso.Emitir(Sujeto(), Usuario, Ahora);

    [Fact]
    public void Emitir_deja_la_credencial_emitida_en_la_serie_uno_con_la_instantanea_del_sujeto()
    {
        var sujeto = Sujeto("  Ana Pérez  ", " 12345678Z ");

        var credencial = CredencialAcceso.Emitir(sujeto, Usuario, Ahora);

        credencial.Estado.Should().Be(EstadoCredencialAcceso.Emitida);
        credencial.Serie.Should().Be(1);
        credencial.TenantOrigenId.Should().Be(sujeto.TenantOrigenId);
        credencial.TrabajadorOrigenId.Should().Be(sujeto.TrabajadorOrigenId);
        credencial.SujetoNombre.Should().Be("Ana Pérez");
        credencial.SujetoDocumentoIdentidad.Should().Be("12345678Z");
        credencial.EmitidaPorUsuarioId.Should().Be(Usuario);
        credencial.EmitidaEnUtc.Should().Be(Ahora);
        credencial.MotivoRevocacion.Should().BeNull();
        credencial.RevocadaEnUtc.Should().BeNull();
    }

    [Fact]
    public void El_documento_de_identidad_es_opcional_y_un_vacio_se_guarda_como_nulo()
    {
        CredencialAcceso.Emitir(Sujeto(documento: "  "), Usuario, Ahora).SujetoDocumentoIdentidad.Should().BeNull();
        CredencialAcceso.Emitir(Sujeto(documento: null), Usuario, Ahora).SujetoDocumentoIdentidad.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Emitir_exige_el_nombre_del_sujeto(string nombre)
    {
        var emitir = () => CredencialAcceso.Emitir(Sujeto(nombre), Usuario, Ahora);

        emitir.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Emitir_rechaza_ids_vacios_y_longitudes_excesivas()
    {
        var s = Sujeto();

        var sinTenant = () => CredencialAcceso.Emitir(s with { TenantOrigenId = Guid.Empty }, Usuario, Ahora);
        var sinTrabajador = () => CredencialAcceso.Emitir(s with { TrabajadorOrigenId = Guid.Empty }, Usuario, Ahora);
        var sinUsuario = () => CredencialAcceso.Emitir(s, Guid.Empty, Ahora);
        var nombreLargo = () => CredencialAcceso.Emitir(
            s with { Nombre = new string('a', CredencialAcceso.LongitudMaximaSujetoNombre + 1) }, Usuario, Ahora);
        var documentoLargo = () => CredencialAcceso.Emitir(
            s with { DocumentoIdentidad = new string('1', CredencialAcceso.LongitudMaximaDocumentoIdentidad + 1) }, Usuario, Ahora);

        sinTenant.Should().Throw<ArgumentException>();
        sinTrabajador.Should().Throw<ArgumentException>();
        sinUsuario.Should().Throw<ArgumentException>();
        nombreLargo.Should().Throw<ArgumentException>();
        documentoLargo.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Regenerar_sube_la_serie_y_trae_la_instantanea_al_dia_sin_cambiar_el_estado()
    {
        var credencial = Emitida();
        var otro = Guid.NewGuid();
        var luego = Ahora.AddHours(2);

        credencial.Regenerar("Ana María Pérez", null, otro, luego);
        credencial.Regenerar("Ana María Pérez", "87654321X", otro, luego.AddMinutes(1));

        credencial.Serie.Should().Be(3);
        credencial.Estado.Should().Be(EstadoCredencialAcceso.Emitida);
        credencial.SujetoNombre.Should().Be("Ana María Pérez");
        credencial.SujetoDocumentoIdentidad.Should().Be("87654321X");
        credencial.RegeneradaPorUsuarioId.Should().Be(otro);
        credencial.RegeneradaEnUtc.Should().Be(luego.AddMinutes(1));
    }

    [Fact]
    public void Una_credencial_revocada_o_caducada_no_se_regenera_ni_se_reactiva()
    {
        var revocada = Emitida();
        revocada.Revocar(MotivoRevocacionCredencial.PerdidaOExtravio, Usuario, Ahora);
        var caducada = Emitida();
        caducada.Caducar(Ahora);

        foreach (var credencial in new[] { revocada, caducada })
        {
            var serie = credencial.Serie;
            var estado = credencial.Estado;

            ((Action)(() => credencial.Regenerar("Ana", null, Usuario, Ahora))).Should().Throw<InvalidOperationException>();
            ((Action)(() => credencial.Revocar(MotivoRevocacionCredencial.UsoIndebido, Usuario, Ahora))).Should().Throw<InvalidOperationException>();
            ((Action)(() => credencial.Caducar(Ahora))).Should().Throw<InvalidOperationException>();

            credencial.Serie.Should().Be(serie, "un intento rechazado no sube la serie");
            credencial.Estado.Should().Be(estado);
        }
    }

    [Fact]
    public void Revocar_guarda_el_motivo_cerrado_quien_y_cuando_y_no_toca_la_serie()
    {
        var credencial = Emitida();
        var revisor = Guid.NewGuid();
        var luego = Ahora.AddDays(1);

        credencial.Revocar(MotivoRevocacionCredencial.UsoIndebido, revisor, luego);

        credencial.Estado.Should().Be(EstadoCredencialAcceso.Revocada);
        credencial.MotivoRevocacion.Should().Be(MotivoRevocacionCredencial.UsoIndebido);
        credencial.RevocadaPorUsuarioId.Should().Be(revisor);
        credencial.RevocadaEnUtc.Should().Be(luego);
        credencial.Serie.Should().Be(1);
    }

    [Fact]
    public void El_motivo_de_revocacion_es_un_enum_cerrado_y_un_valor_fuera_de_la_lista_se_rechaza()
    {
        var credencial = Emitida();

        var revocar = () => credencial.Revocar((MotivoRevocacionCredencial)99, Usuario, Ahora);

        revocar.Should().Throw<ArgumentException>();
        credencial.Estado.Should().Be(EstadoCredencialAcceso.Emitida);
        typeof(CredencialAcceso).GetProperty(nameof(CredencialAcceso.MotivoRevocacion))!.PropertyType
            .Should().Be(typeof(MotivoRevocacionCredencial?), "nunca texto libre");
    }

    [Fact]
    public void Revocar_y_regenerar_exigen_usuario()
    {
        var credencial = Emitida();

        ((Action)(() => credencial.Revocar(MotivoRevocacionCredencial.Otro, Guid.Empty, Ahora))).Should().Throw<ArgumentException>();
        ((Action)(() => credencial.Regenerar("Ana", null, Guid.Empty, Ahora))).Should().Throw<ArgumentException>();
        credencial.Estado.Should().Be(EstadoCredencialAcceso.Emitida);
        credencial.Serie.Should().Be(1);
    }

    [Fact]
    public void Caducar_deja_la_credencial_caducada_con_su_momento()
    {
        var credencial = Emitida();

        credencial.Caducar(Ahora.AddDays(30));

        credencial.Estado.Should().Be(EstadoCredencialAcceso.Caducada);
        credencial.CaducadaEnUtc.Should().Be(Ahora.AddDays(30));
        credencial.MotivoRevocacion.Should().BeNull();
    }

    /// <summary>
    /// Los valores del enum son los que el CHECK de la base admite, escritos tal cual: añadir un valor sin su CHECK deja una
    /// credencial que el dominio guarda y la base rechaza.
    /// </summary>
    [Fact]
    public void Los_valores_de_estado_y_de_motivo_son_los_que_admite_la_base()
    {
        Enum.GetNames<EstadoCredencialAcceso>().Should().BeEquivalentTo("Emitida", "Revocada", "Caducada");
        Enum.GetNames<MotivoRevocacionCredencial>().Should().BeEquivalentTo(
            "PerdidaOExtravio", "UsoIndebido", "FinDeLaRelacion", "Otro");
    }
}
