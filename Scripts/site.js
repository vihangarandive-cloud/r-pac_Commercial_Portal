// /Scripts/site.js
$(document).ready(function () {
    // Menu toggle for responsive mobile layout
    $("#menu-toggle").click(function (e) {
        e.preventDefault();
        $("#wrapper").toggleClass("toggled");
    });
});
