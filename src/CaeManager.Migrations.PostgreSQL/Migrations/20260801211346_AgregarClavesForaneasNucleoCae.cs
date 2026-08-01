using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarClavesForaneasNucleoCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Visitas_TenantId_Id",
                table: "Visitas",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Vehiculos_TenantId_Id",
                table: "Vehiculos",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Trabajadores_TenantId_Id",
                table: "Trabajadores",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TiposDocumento_TenantId_Id",
                table: "TiposDocumento",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Subcontratas_TenantId_Id",
                table: "Subcontratas",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Proyectos_TenantId_Id",
                table: "Proyectos",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Empresas_TenantId_Id",
                table: "Empresas",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Documentos_TenantId_Id",
                table: "Documentos",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Clientes_TenantId_Id",
                table: "Clientes",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Centros_TenantId_Id",
                table: "Centros",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitasTrabajadores_TenantId_TrabajadorId",
                table: "VisitasTrabajadores",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_TenantId_CentroId",
                table: "Visitas",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_TenantId_Id",
                table: "Visitas",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_TenantId_EmpresaId",
                table: "Vehiculos",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_TenantId_SubcontrataId",
                table: "Vehiculos",
                columns: new[] { "TenantId", "SubcontrataId" });

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_Id",
                table: "Trabajadores",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_SubcontrataId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "SubcontrataId" });

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoCentros_TenantId_CentroId",
                table: "TiposDocumentoCentros",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumento_TenantId_Id",
                table: "TiposDocumento",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_TenantId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_TenantId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_TenantId_Id",
                table: "Subcontratas",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosDocumentales_TenantId_CentroId",
                table: "RequisitosDocumentales",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosTecnicos_TenantId_TrabajadorId",
                table: "ProyectosTecnicos",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_TenantId_CentroId",
                table: "Proyectos",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_Proyectos_TenantId_Id",
                table: "Proyectos",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Incidencias_TenantId_CentroId",
                table: "Incidencias",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidencias_TenantId_TrabajadorId",
                table: "Incidencias",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluaciones_TenantId_CentroId",
                table: "Evaluaciones",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluaciones_TenantId_TrabajadorId",
                table: "Evaluaciones",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_TenantId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_TenantId_Id",
                table: "Empresas",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_ClienteId",
                table: "Documentos",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_EmpresaId",
                table: "Documentos",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_Id",
                table: "Documentos",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_ProyectoId",
                table: "Documentos",
                columns: new[] { "TenantId", "ProyectoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_TrabajadorId",
                table: "Documentos",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_VehiculoId",
                table: "Documentos",
                columns: new[] { "TenantId", "VehiculoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_TenantId_Id",
                table: "Clientes",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Centros_TenantId_ClienteId",
                table: "Centros",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_Centros_TenantId_EmpresaId",
                table: "Centros",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_Centros_TenantId_Id",
                table: "Centros",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TenantId_CentroId",
                table: "Asignaciones",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_Alertas_TenantId_DocumentoId",
                table: "Alertas",
                columns: new[] { "TenantId", "DocumentoId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Alertas_Documentos_TenantId_DocumentoId",
                table: "Alertas",
                columns: new[] { "TenantId", "DocumentoId" },
                principalTable: "Documentos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Asignaciones_Centros_TenantId_CentroId",
                table: "Asignaciones",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Asignaciones_Trabajadores_TenantId_TrabajadorId",
                table: "Asignaciones",
                columns: new[] { "TenantId", "TrabajadorId" },
                principalTable: "Trabajadores",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CanalesGestionDocumental_Centros_TenantId_CentroId",
                table: "CanalesGestionDocumental",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Centros_Clientes_TenantId_ClienteId",
                table: "Centros",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Centros_Empresas_TenantId_EmpresaId",
                table: "Centros",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Clientes_TenantId_ClienteId",
                table: "Documentos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Empresas_TenantId_EmpresaId",
                table: "Documentos",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Proyectos_TenantId_ProyectoId",
                table: "Documentos",
                columns: new[] { "TenantId", "ProyectoId" },
                principalTable: "Proyectos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_TiposDocumento_TenantId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "TenantId", "TipoDocumentoId" },
                principalTable: "TiposDocumento",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Trabajadores_TenantId_TrabajadorId",
                table: "Documentos",
                columns: new[] { "TenantId", "TrabajadorId" },
                principalTable: "Trabajadores",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Vehiculos_TenantId_VehiculoId",
                table: "Documentos",
                columns: new[] { "TenantId", "VehiculoId" },
                principalTable: "Vehiculos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmpresasClientes_Clientes_TenantId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmpresasClientes_Empresas_TenantId_EmpresaId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Evaluaciones_Centros_TenantId_CentroId",
                table: "Evaluaciones",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Evaluaciones_Trabajadores_TenantId_TrabajadorId",
                table: "Evaluaciones",
                columns: new[] { "TenantId", "TrabajadorId" },
                principalTable: "Trabajadores",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Incidencias_Centros_TenantId_CentroId",
                table: "Incidencias",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Incidencias_Trabajadores_TenantId_TrabajadorId",
                table: "Incidencias",
                columns: new[] { "TenantId", "TrabajadorId" },
                principalTable: "Trabajadores",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Proyectos_Centros_TenantId_CentroId",
                table: "Proyectos",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Proyectos_Clientes_TenantId_ClienteId",
                table: "Proyectos",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProyectosTecnicos_Proyectos_TenantId_ProyectoId",
                table: "ProyectosTecnicos",
                columns: new[] { "TenantId", "ProyectoId" },
                principalTable: "Proyectos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProyectosTecnicos_Trabajadores_TenantId_TrabajadorId",
                table: "ProyectosTecnicos",
                columns: new[] { "TenantId", "TrabajadorId" },
                principalTable: "Trabajadores",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RequisitosDocumentales_Centros_TenantId_CentroId",
                table: "RequisitosDocumentales",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Clientes_TenantId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasClientes_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasEmpresas_Empresas_TenantId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubcontratasEmpresas_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TarifasCliente_Clientes_TenantId_ClienteId",
                table: "TarifasCliente",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TiposDocumentoCentros_Centros_TenantId_CentroId",
                table: "TiposDocumentoCentros",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TiposDocumentoCentros_TiposDocumento_TenantId_TipoDocumento~",
                table: "TiposDocumentoCentros",
                columns: new[] { "TenantId", "TipoDocumentoId" },
                principalTable: "TiposDocumento",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Trabajadores_Empresas_TenantId_EmpresaId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Trabajadores_Subcontratas_TenantId_SubcontrataId",
                table: "Trabajadores",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Vehiculos_Empresas_TenantId_EmpresaId",
                table: "Vehiculos",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Vehiculos_Subcontratas_TenantId_SubcontrataId",
                table: "Vehiculos",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Subcontratas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Visitas_Centros_TenantId_CentroId",
                table: "Visitas",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VisitasTrabajadores_Trabajadores_TenantId_TrabajadorId",
                table: "VisitasTrabajadores",
                columns: new[] { "TenantId", "TrabajadorId" },
                principalTable: "Trabajadores",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VisitasTrabajadores_Visitas_TenantId_VisitaId",
                table: "VisitasTrabajadores",
                columns: new[] { "TenantId", "VisitaId" },
                principalTable: "Visitas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Alertas_Documentos_TenantId_DocumentoId",
                table: "Alertas");

            migrationBuilder.DropForeignKey(
                name: "FK_Asignaciones_Centros_TenantId_CentroId",
                table: "Asignaciones");

            migrationBuilder.DropForeignKey(
                name: "FK_Asignaciones_Trabajadores_TenantId_TrabajadorId",
                table: "Asignaciones");

            migrationBuilder.DropForeignKey(
                name: "FK_CanalesGestionDocumental_Centros_TenantId_CentroId",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropForeignKey(
                name: "FK_Centros_Clientes_TenantId_ClienteId",
                table: "Centros");

            migrationBuilder.DropForeignKey(
                name: "FK_Centros_Empresas_TenantId_EmpresaId",
                table: "Centros");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Clientes_TenantId_ClienteId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Empresas_TenantId_EmpresaId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Proyectos_TenantId_ProyectoId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_TiposDocumento_TenantId_TipoDocumentoId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Trabajadores_TenantId_TrabajadorId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Vehiculos_TenantId_VehiculoId",
                table: "Documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_EmpresasClientes_Clientes_TenantId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_EmpresasClientes_Empresas_TenantId_EmpresaId",
                table: "EmpresasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_Evaluaciones_Centros_TenantId_CentroId",
                table: "Evaluaciones");

            migrationBuilder.DropForeignKey(
                name: "FK_Evaluaciones_Trabajadores_TenantId_TrabajadorId",
                table: "Evaluaciones");

            migrationBuilder.DropForeignKey(
                name: "FK_Incidencias_Centros_TenantId_CentroId",
                table: "Incidencias");

            migrationBuilder.DropForeignKey(
                name: "FK_Incidencias_Trabajadores_TenantId_TrabajadorId",
                table: "Incidencias");

            migrationBuilder.DropForeignKey(
                name: "FK_Proyectos_Centros_TenantId_CentroId",
                table: "Proyectos");

            migrationBuilder.DropForeignKey(
                name: "FK_Proyectos_Clientes_TenantId_ClienteId",
                table: "Proyectos");

            migrationBuilder.DropForeignKey(
                name: "FK_ProyectosTecnicos_Proyectos_TenantId_ProyectoId",
                table: "ProyectosTecnicos");

            migrationBuilder.DropForeignKey(
                name: "FK_ProyectosTecnicos_Trabajadores_TenantId_TrabajadorId",
                table: "ProyectosTecnicos");

            migrationBuilder.DropForeignKey(
                name: "FK_RequisitosDocumentales_Centros_TenantId_CentroId",
                table: "RequisitosDocumentales");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Clientes_TenantId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasClientes_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasClientes");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasEmpresas_Empresas_TenantId_EmpresaId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropForeignKey(
                name: "FK_SubcontratasEmpresas_Subcontratas_TenantId_SubcontrataId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropForeignKey(
                name: "FK_TarifasCliente_Clientes_TenantId_ClienteId",
                table: "TarifasCliente");

            migrationBuilder.DropForeignKey(
                name: "FK_TiposDocumentoCentros_Centros_TenantId_CentroId",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropForeignKey(
                name: "FK_TiposDocumentoCentros_TiposDocumento_TenantId_TipoDocumento~",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropForeignKey(
                name: "FK_Trabajadores_Empresas_TenantId_EmpresaId",
                table: "Trabajadores");

            migrationBuilder.DropForeignKey(
                name: "FK_Trabajadores_Subcontratas_TenantId_SubcontrataId",
                table: "Trabajadores");

            migrationBuilder.DropForeignKey(
                name: "FK_Vehiculos_Empresas_TenantId_EmpresaId",
                table: "Vehiculos");

            migrationBuilder.DropForeignKey(
                name: "FK_Vehiculos_Subcontratas_TenantId_SubcontrataId",
                table: "Vehiculos");

            migrationBuilder.DropForeignKey(
                name: "FK_Visitas_Centros_TenantId_CentroId",
                table: "Visitas");

            migrationBuilder.DropForeignKey(
                name: "FK_VisitasTrabajadores_Trabajadores_TenantId_TrabajadorId",
                table: "VisitasTrabajadores");

            migrationBuilder.DropForeignKey(
                name: "FK_VisitasTrabajadores_Visitas_TenantId_VisitaId",
                table: "VisitasTrabajadores");

            migrationBuilder.DropIndex(
                name: "IX_VisitasTrabajadores_TenantId_TrabajadorId",
                table: "VisitasTrabajadores");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Visitas_TenantId_Id",
                table: "Visitas");

            migrationBuilder.DropIndex(
                name: "IX_Visitas_TenantId_CentroId",
                table: "Visitas");

            migrationBuilder.DropIndex(
                name: "IX_Visitas_TenantId_Id",
                table: "Visitas");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Vehiculos_TenantId_Id",
                table: "Vehiculos");

            migrationBuilder.DropIndex(
                name: "IX_Vehiculos_TenantId_EmpresaId",
                table: "Vehiculos");

            migrationBuilder.DropIndex(
                name: "IX_Vehiculos_TenantId_SubcontrataId",
                table: "Vehiculos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Trabajadores_TenantId_Id",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_EmpresaId",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_Id",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_SubcontrataId",
                table: "Trabajadores");

            migrationBuilder.DropIndex(
                name: "IX_TiposDocumentoCentros_TenantId_CentroId",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TiposDocumento_TenantId_Id",
                table: "TiposDocumento");

            migrationBuilder.DropIndex(
                name: "IX_TiposDocumento_TenantId_Id",
                table: "TiposDocumento");

            migrationBuilder.DropIndex(
                name: "IX_SubcontratasEmpresas_TenantId_EmpresaId",
                table: "SubcontratasEmpresas");

            migrationBuilder.DropIndex(
                name: "IX_SubcontratasClientes_TenantId_ClienteId",
                table: "SubcontratasClientes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Subcontratas_TenantId_Id",
                table: "Subcontratas");

            migrationBuilder.DropIndex(
                name: "IX_Subcontratas_TenantId_Id",
                table: "Subcontratas");

            migrationBuilder.DropIndex(
                name: "IX_RequisitosDocumentales_TenantId_CentroId",
                table: "RequisitosDocumentales");

            migrationBuilder.DropIndex(
                name: "IX_ProyectosTecnicos_TenantId_TrabajadorId",
                table: "ProyectosTecnicos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Proyectos_TenantId_Id",
                table: "Proyectos");

            migrationBuilder.DropIndex(
                name: "IX_Proyectos_TenantId_CentroId",
                table: "Proyectos");

            migrationBuilder.DropIndex(
                name: "IX_Proyectos_TenantId_Id",
                table: "Proyectos");

            migrationBuilder.DropIndex(
                name: "IX_Incidencias_TenantId_CentroId",
                table: "Incidencias");

            migrationBuilder.DropIndex(
                name: "IX_Incidencias_TenantId_TrabajadorId",
                table: "Incidencias");

            migrationBuilder.DropIndex(
                name: "IX_Evaluaciones_TenantId_CentroId",
                table: "Evaluaciones");

            migrationBuilder.DropIndex(
                name: "IX_Evaluaciones_TenantId_TrabajadorId",
                table: "Evaluaciones");

            migrationBuilder.DropIndex(
                name: "IX_EmpresasClientes_TenantId_ClienteId",
                table: "EmpresasClientes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Empresas_TenantId_Id",
                table: "Empresas");

            migrationBuilder.DropIndex(
                name: "IX_Empresas_TenantId_Id",
                table: "Empresas");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Documentos_TenantId_Id",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_ClienteId",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_EmpresaId",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_Id",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_ProyectoId",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_TipoDocumentoId",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_TrabajadorId",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_VehiculoId",
                table: "Documentos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Clientes_TenantId_Id",
                table: "Clientes");

            migrationBuilder.DropIndex(
                name: "IX_Clientes_TenantId_Id",
                table: "Clientes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Centros_TenantId_Id",
                table: "Centros");

            migrationBuilder.DropIndex(
                name: "IX_Centros_TenantId_ClienteId",
                table: "Centros");

            migrationBuilder.DropIndex(
                name: "IX_Centros_TenantId_EmpresaId",
                table: "Centros");

            migrationBuilder.DropIndex(
                name: "IX_Centros_TenantId_Id",
                table: "Centros");

            migrationBuilder.DropIndex(
                name: "IX_Asignaciones_TenantId_CentroId",
                table: "Asignaciones");

            migrationBuilder.DropIndex(
                name: "IX_Alertas_TenantId_DocumentoId",
                table: "Alertas");
        }
    }
}
