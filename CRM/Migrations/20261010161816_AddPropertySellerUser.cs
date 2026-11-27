using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRM.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertySellerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SellerUserId",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_SellerUserId",
                table: "Properties",
                column: "SellerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_Users_SellerUserId",
                table: "Properties",
                column: "SellerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Properties_Users_SellerUserId",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Properties_SellerUserId",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "SellerUserId",
                table: "Properties");
        }
    }
}
