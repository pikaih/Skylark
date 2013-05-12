﻿<%@ Page Title="离线下载你管视频" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true" CodeBehind="YouTube.aspx.cs" Inherits="Mygod.Skylark.Offline.YouTube" %>
<asp:Content ID="Content1" ContentPlaceHolderID="Head" runat="server">
    <style type="text/css">
        #link-box {
            width: 100%;
        }
    </style>
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="ContentPlaceHolder1" runat="server">
    <h2 class="center">离线下载你管视频</h2>
    <p><input type="text" id="link-box" value="http://www.youtube.com/results?search_query=" /></p>
    <p class="center"><button type="button" id="magic-button">获取该页上全部视频</button></p>
    <script type="text/javascript">
        var url = getQueryStringRegExp("URL");
        if (url) $("#link-box").val($.base64reversed.decode(url));
        $("#magic-button").click(function () {
            var url = "/NiGuan/" + $.base64reversed.encode($("#link-box").val());
            var str = getQueryString();
            if (str) url += '?' + str;
            location.href = url;
        });
    </script>
    <div>想要直接下载你管视频？试试<a href="http://mygod.apphb.com/#Product-66">掩耳</a>！</div>
    <% GetEmAll(); %>
</asp:Content>