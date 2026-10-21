using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Exportación de trabajadores extranjeros del mes del Cuadro de dirección (decisión de Chris, 2026-10-20):
    /// <list type="bullet">
    /// <item><c>Trabajadores.MarcaExtranjeroCobrable</c>: la casilla «Trabajador extranjero (cobrable)». NULL = decide el
    /// documento (<c>CriterioExtranjeroCobrable.PorDocumento</c>). Dato personal: entra en
    /// <c>Trabajador.CamposDeDatosPersonales</c> y la función que enmascara el historial de un Trabajador anonimizado se
    /// redefine con él (mismo cuerpo que <see cref="AnadeContactoEmergenciaATrabajador"/>).</item>
    /// <item><c>VisitasTrabajadores.FechaAsignacionUtc</c> y <c>GestionadoPorUsuarioId</c>: cuándo y quién (el Actor real,
    /// también en impersonación) asignó el Trabajador a la Visita. Sin relleno: las filas anteriores quedan a NULL y la
    /// exportación recurre al registro de auditoría «Creado» de la asignación y, si falta, al alta de la Visita.</item>
    /// </list>
    /// Solo añade columnas a tablas que ya tienen RLS forzada por TenantId: no toca políticas, GRANT ni roles.
    /// </summary>
    public partial class AnadeExtranjeroCobrableYAsignacionVisita : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FechaAsignacionUtc",
                table: "VisitasTrabajadores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GestionadoPorUsuarioId",
                table: "VisitasTrabajadores",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MarcaExtranjeroCobrable",
                table: "Trabajadores",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql(AnadeContactoEmergenciaATrabajador.DefinicionDeLaFuncion(CamposConExtranjeroCobrable));
        }

        private const string CamposAnteriores =
            """
            'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                                'Email', 'Telefono', 'Observaciones', 'Notas',
                                'ContactoEmergenciaNombre', 'ContactoEmergenciaTelefono', 'Puesto', 'OficioEmpresaId',
                                'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                                'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                                'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo'
            """;

        private const string CamposConExtranjeroCobrable =
            """
            'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                                'Email', 'Telefono', 'Observaciones', 'Notas',
                                'ContactoEmergenciaNombre', 'ContactoEmergenciaTelefono', 'Puesto', 'OficioEmpresaId',
                                'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                                'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                                'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo',
                                'MarcaExtranjeroCobrable'
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AnadeContactoEmergenciaATrabajador.DefinicionDeLaFuncion(CamposAnteriores));

            migrationBuilder.DropColumn(
                name: "FechaAsignacionUtc",
                table: "VisitasTrabajadores");

            migrationBuilder.DropColumn(
                name: "GestionadoPorUsuarioId",
                table: "VisitasTrabajadores");

            migrationBuilder.DropColumn(
                name: "MarcaExtranjeroCobrable",
                table: "Trabajadores");
        }
    }
}
