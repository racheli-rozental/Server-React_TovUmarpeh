// using System.Runtime.CompilerServices;
// using Microsoft.AspNetCore.Cors;
using Microsoft.EntityFrameworkCore;
using TovUmarpeh;
using Amazon.S3;
using Amazon.S3.Model;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin", policy =>
    {
        policy.WithOrigins("https://manager-tuvumarpeh.onrender.com") // אפשר גישה רק מכתובת זו
              .AllowAnyHeader() // אפשר כל סוגי הכותרות
              .AllowAnyMethod(); // אפשר כל סוגי הבקשות (GET, POST וכו')
    });
});
DotNetEnv.Env.Load();
    builder.Services.AddDbContext<UsersDBContext>(options =>
        options.UseMySql(builder.Configuration.GetConnectionString("UsersDb"), 
        ServerVersion.Parse("8.0.41-mysql")));

   


var app = builder.Build();
app.UseCors("AllowSpecificOrigin");
//users
app.MapGet("/users", async (UsersDBContext context) =>
{
    return await context.UsersTables.ToListAsync();
});
app.MapGet("/users/{id}", async (UsersDBContext context, string id) =>
{
    return await context.UsersTables.FindAsync(id) switch
    {
        UsersTable user => Results.Ok(user),
        _ => Results.NotFound()
    };
});
app.MapPost("/users",  async (UsersDBContext context, HttpRequest request) =>
{
    using var transaction = await context.Database.BeginTransactionAsync();
    try
    {
        if (!int.TryParse(request.Form["IdNumber"], out var idNumber))
        {
            return Results.BadRequest("Invalid IdNumber format.");
        }

        var user = new UsersTable
        {
            IdNumber = idNumber,
            FirstName = request.Form["FirstName"],
            LastName = request.Form["LastName"],
            Address = request.Form["Address"],
            Phone = request.Form["Phone"]!,
            City = request.Form["City"]!,
            Email = request.Form["Email"]!,
            BirthDate = request.Form["BirthDate"]!
        };

        context.UsersTables.Add(user);

        var s3Client = new AmazonS3Client(
            Environment.GetEnvironmentVariable("KEY_ID"),
            Environment.GetEnvironmentVariable("ACCESS_KEY"),
            Amazon.RegionEndpoint.USEast1);

        var medicationsUrl = string.Empty;
        var agreementUrl = string.Empty;
        var personalDetailsUrl = string.Empty;
        var identityUrl = string.Empty;

        if (request.Form.Files.Count > 0)
        {
            for (int i = 0; i < request.Form.Files.Count; i++)
            {
                var file = request.Form.Files[i];

                if (file.Length > 5 * 1024 * 1024)
                {
                    return Results.BadRequest($"File {file.FileName} is too large. Maximum size is 5MB.");
                }

                var uploadRequest = new PutObjectRequest
                {
                    BucketName = "tovumarpeh",
                    Key = file.FileName,
                    InputStream = file.OpenReadStream(),
                    ContentType = file.ContentType
                };

                var response = await s3Client.PutObjectAsync(uploadRequest);
                if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
                {
                    return Results.Problem($"Error uploading file {file.FileName} to S3. Status code: {response.HttpStatusCode}");
                }

                var fileUrl = $"https://tovumarpeh.s3.amazonaws.com/{file.FileName}";

                if (i == 0) medicationsUrl = fileUrl;
                else if (i == 1) agreementUrl = fileUrl;
                else if (i == 2) personalDetailsUrl = fileUrl;
                else if (i == 3) identityUrl = fileUrl;
            }
        }

        var userFile = new UsersFile
        {
            IdNumber = idNumber,
            Medications = medicationsUrl,
            Agreement = agreementUrl,
            PersonalDetails = personalDetailsUrl,
            Identity = identityUrl
        };

        context.UsersFiles.Add(userFile);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        return Results.Created($"/users/files/{userFile.Id}", new { userFile });
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return Results.Problem($"An error occurred: {ex.Message}");
    }
});

app.MapGet("/files/{fileName}", async ( UsersDBContext context, HttpContext httpContext,string fileName) =>
{
    var s3Client = new AmazonS3Client(
        Environment.GetEnvironmentVariable("KEY_ID"),
        Environment.GetEnvironmentVariable("ACCESS_KEY"),
        Amazon.RegionEndpoint.USEast1);

    var request = new GetObjectRequest
    {
        BucketName = "tovumarpeh",
        Key = fileName
    };

    using var response = await s3Client.GetObjectAsync(request);
    using var responseStream = response.ResponseStream;
    using var memoryStream = new MemoryStream();
    await responseStream.CopyToAsync(memoryStream);
    
    return Results.File(memoryStream.ToArray(), response.Headers["Content-Type"], fileName);
});

app.MapPut("/users/{id}", async (UsersDBContext context, int id, UsersTable user) =>
{
    if (id != user.IdNumber)
    {
        return Results.BadRequest();
    }

    var existingUser = await context.UsersTables.FindAsync(id);
    if (existingUser == null)
    {
        return Results.NotFound();
    }
    existingUser.FirstName = user.FirstName;
    existingUser.LastName = user.LastName;
    existingUser.Address= user.Address;
    existingUser.Phone = user.Phone;
    existingUser.City = user.City;
    existingUser.BirthDate=user.BirthDate;
    existingUser.Email=user.Email;
    

    context.Entry(existingUser).State = EntityState.Modified;
    await context.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/users/{id}", async (UsersDBContext context, int id) =>
{
    var user = await context.UsersTables.FindAsync(id);
    if (user == null)
    {
        return Results.NotFound();
    }
    
    // מחפש את הקובץ של המשתמש
    var file = await context.UsersFiles.FindAsync(id);
    if (file != null)
    {
        context.UsersFiles.Remove(file); // מסיר את הקובץ היחיד
    }

    // מחפש את הרשומות התלויות בטבלת enrollments
    var enrollments = await context.Enrollments.Where(e => e.IdNumber == user.IdNumber).ToListAsync();
    if (enrollments.Any())
    {
        context.Enrollments.RemoveRange(enrollments); // מסיר את כל הרשומות התלויות
    }

    context.UsersTables.Remove(user); // מסיר את המשתמש
    
    await context.SaveChangesAsync();
    return Results.NoContent();
});


//files
app.MapGet("/file", async (UsersDBContext context) =>
{
    return await context.UsersFiles.ToListAsync();
});
app.MapGet("/file/{idNumber}", async (UsersDBContext context, int idNumber) =>
{
    var userFile = await context.UsersFiles.FirstOrDefaultAsync(uf => uf.IdNumber == idNumber);
    return userFile switch
    {
        UsersFile user => Results.Ok(user),
        _ => Results.NotFound()
    };
});

app.MapPost("/file", async (UsersDBContext context, UsersFile file) =>
{
    context.UsersFiles.Add(file);
    await context.SaveChangesAsync();
    return Results.Created($"/users/{file.IdNumber}", file);
});
//activities
  app.MapPost("/addActivity", async (UsersDBContext context, Activity activity) =>
    {
        context.Activities.Add(activity);
        await context.SaveChangesAsync();
        return Results.Created($"/addactivity/{activity.IdActivities}", activity);
    });

   app.MapGet("/activity", async (UsersDBContext context) =>
    {
        return await context.Activities.ToListAsync();
    });

    app.MapGet("/getActivity/{id}", async (UsersDBContext context, int id) =>
    {
        return await context.Activities.FindAsync(id) switch
        {
            Activity activity => Results.Ok(activity),
            _ => Results.NotFound()
        };
    });
app.MapPut("/updateActivity/{id}", async (UsersDBContext context, int id, Activity activity) =>
{
    if (id != activity.IdActivities)
    {
        return Results.BadRequest();
    }


    var existingActivity = await context.Activities.FindAsync(id);
    if (existingActivity == null)
    {
        return Results.NotFound();
    }
    existingActivity.NameActivity = activity.NameActivity;
    existingActivity.DateActivity = activity.DateActivity;
    existingActivity.DetailsActivity = activity.DetailsActivity;
    existingActivity.Max = activity.Max;
    existingActivity.Enrollments = activity.Enrollments;

    context.Entry(existingActivity).State = EntityState.Modified;
    await context.SaveChangesAsync();
    return Results.NoContent();
});


    app.MapDelete("/deleteActivity/{id}", async (UsersDBContext context, int id) =>
{
    var activity = await context.Activities.FindAsync(id);
    if (activity == null)
    {
        return Results.NotFound();
    }
    var enrollments = context.Enrollments.Where(e => e.IdActivities == id).ToList();
    context.Enrollments.RemoveRange(enrollments);
    context.Activities.Remove(activity);
    await context.SaveChangesAsync();

    return Results.NoContent();
});


app.MapGet("/enrollments/{activityId}", async (UsersDBContext context, int activityId) =>
{
    var enrollments = await context.Enrollments
        .Where(e => e.IdActivities == activityId)
        .Include(e => e.IdNumberNavigation)
        .Select(e => new 
        {
            e.EnrollmentId,
            e.IdNumberNavigation!.IdNumber,
            e.IdNumberNavigation.FirstName,
            e.IdNumberNavigation.LastName,
         
        })
        .ToListAsync();

    if (enrollments.Count == 0)
    {
        return Results.NotFound("אין הרשמות לפעילות זו.");
    }

    return Results.Ok(enrollments);
});

app.MapGet("/enroll", async (UsersDBContext context) =>
{
    return await context.Enrollments.ToListAsync();
});



    app.MapPost("/activity/{id}/children", async (UsersDBContext context, int id, Enrollment enrollment) =>
{
    var activity = await context.Activities.FindAsync(id);
    if (activity == null)
    {
        return Results.NotFound();
    }

    var child = await context.UsersTables.FindAsync(enrollment.IdNumber);
    if (child == null)
    {
        return Results.NotFound();
    }

    enrollment.IdActivities = id;
    context.Enrollments.Add(enrollment);
    await context.SaveChangesAsync();
    return Results.Created($"/activity/{id}/children/{enrollment.IdNumber}", enrollment);
});
    app.MapDelete("/activity/{IdActivities}/children/{IdNumber}", async (UsersDBContext context, int IdActivities, int IdNumber) =>
{
    var enrollment = await context.Enrollments
        .FirstOrDefaultAsync(e => e.IdActivities == IdActivities && e.IdNumber == IdNumber); // הנח שהשדות הם ActivityId ו-NumberId
    if (enrollment == null)
    {
        return Results.NotFound();
    }

    context.Enrollments.Remove(enrollment);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();
