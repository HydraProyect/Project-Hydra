using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// PD-A5 (2026-09-17): renombra el valor persistido del enum
    /// <c>PropositoDelegacion.Comercial</c> a <c>OperadorExterno</c>. Sin
    /// cambio de esquema —la columna sigue siendo <c>varchar(20)</c>— porque
    /// se persiste el NOMBRE del enum, no su ordinal
    /// (<c>DelegacionTenantConfiguration.cs</c>, <c>HasConversion&lt;string&gt;()</c>).
    ///
    /// El renombrado responde a una colisión de vocabulario real, no
    /// cosmética: este valor siempre representó una Consultora de PRL
    /// —Operador CAE externo— gestionando la CAE de un Cliente Delegante
    /// (plano de Operación, ADR-011 § 1), nunca el plano Comercial del
    /// contrato de terminología (quién contrata o paga TALVEG). El nombre
    /// anterior invitaba a confundir los dos.
    /// </summary>
    public partial class RenombrarPropositoDelegacionComercial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"DelegacionesTenant\" SET \"Proposito\" = 'OperadorExterno' WHERE \"Proposito\" = 'Comercial';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"DelegacionesTenant\" SET \"Proposito\" = 'Comercial' WHERE \"Proposito\" = 'OperadorExterno';");
        }
    }
}
